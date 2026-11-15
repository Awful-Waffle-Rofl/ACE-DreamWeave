using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

using log4net;

using ACE.Common;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Defs;
using ACE.Server.WorldObjects;

namespace ACE.Server.Frays
{
    /// <summary>
    /// Frays creatures built the way a Thread builds its own, so they get the same attack and skills as Thread
    /// creatures (retail creatures lifted on the stat curve alone posed almost no threat).
    /// A Threads spawn plan is built for the week's family at the camper's level plus the Heat's levels (the same
    /// <see cref="DungeonPopulationBuilder.Build"/> call a speed course makes), and each creature is shaped by
    /// <see cref="ThreadDungeonSpawner.ShapeCreature"/>: the band and archetype standards for its role (attack, defense,
    /// damage, attributes, ratings, resists), the damage ceiling and the combat-trait rules, with no run and no rewards.
    /// The Frays then set its health, Heat, elite and Snarl numbers on top, as before.
    /// Plans are cached by family and level. A family the Threads builder cannot field at a level is remembered and
    /// keeps the Frays' own stat lift (<see cref="FraysLevelScale"/>).
    /// </summary>
    public static class FraysThreadsShape
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>Family key and run level to its plan; null = the builder could not field that family there.</summary>
        private static readonly ConcurrentDictionary<(string Family, int Level), DungeonSpawnPlan> plans = new();

        private static string dungeonId;

        /// <summary>One plan build at a time (the builder reads shared catalogs; a burst of spawns at a new level builds once).</summary>
        private static readonly object BuildGate = new object();

        /// <summary>The run level a Frays creature is shaped at: the Frays target level, inside Threads' run ceiling.</summary>
        public static int RunLevel(int targetLevel) => Math.Clamp(targetLevel, 1, DungeonGemSpec.MaxRunLevel);

        /// <summary>The Threads role a Frays role is shaped as. A Snarl is shaped as an elite (the Frays size its health and hits).</summary>
        public static DungeonRole RoleFor(FraysRole role) => role == FraysRole.Elite || role == FraysRole.Snarl ? DungeonRole.Elite : DungeonRole.Trash;

        /// <summary>The plan entries a role draws from: that role's, else the plan's trash (a plan always has trash).</summary>
        public static IReadOnlyList<DungeonSpawnPlanEntry> EntriesFor(IReadOnlyList<DungeonSpawnPlanEntry> entries, DungeonRole role)
        {
            if (entries == null || entries.Count == 0)
                return Array.Empty<DungeonSpawnPlanEntry>();

            var own = entries.Where(e => e != null && e.Role == role).ToList();

            return own.Count > 0 ? own : entries.Where(e => e != null && e.Role == DungeonRole.Trash).ToList();
        }

        /// <summary>
        /// The entries without those <paramref name="leaveOut"/> names (casters below caster_min_heat). Empty when every
        /// entry is left out: the caller then spawns the family's own pick, which already leaves them out.
        /// </summary>
        public static IReadOnlyList<DungeonSpawnPlanEntry> WithoutLeftOut(IReadOnlyList<DungeonSpawnPlanEntry> entries, Func<uint, bool> leaveOut)
        {
            if (leaveOut == null || entries == null || entries.Count == 0)
                return entries ?? Array.Empty<DungeonSpawnPlanEntry>();

            return entries.Where(e => !leaveOut(e.Wcid)).ToList();
        }

        /// <summary>Drops every cached plan (a settings or catalog change).</summary>
        public static void Clear() => plans.Clear();

        /// <summary>The Threads plan for <paramref name="table"/> at <paramref name="level"/>, or null when there is none.</summary>
        public static DungeonSpawnPlan PlanFor(SpeciesTableDef table, int level)
        {
            if (table?.Id == null)
                return null;

            var runLevel = RunLevel(level);
            if (plans.TryGetValue((table.Id, runLevel), out var known))
                return known;

            lock (BuildGate)
                return plans.GetOrAdd((table.Id, runLevel), key => Build(key.Family, key.Level));
        }

        /// <summary>
        /// Picks the Threads creature for a Frays role from the plan and shapes it. Null when the plan has nothing for it
        /// (the caller then spawns its own pick with the Frays' own lift).
        /// </summary>
        public static (uint Wcid, DungeonSpawnPlanEntry Entry) Pick(DungeonSpawnPlan plan, FraysRole role, Func<uint, bool> leaveOut = null)
        {
            var pool = WithoutLeftOut(EntriesFor(plan?.Entries, RoleFor(role)), leaveOut);

            if (pool.Count == 0)
                return (0, null);

            var entry = pool[ThreadSafeRandom.Next(0, pool.Count - 1)];
            return (entry.Wcid, entry);
        }

        /// <summary>
        /// An entry for the Frays' own pick when the plan fields nothing for its role (a family whose creatures sit far
        /// below the run level: Monouga at 115 under a level 480 run). Threads' band uplift when the plan has a band
        /// standard: skills raised to the band's medians and body damage to its level, as Threads does for a pick below
        /// its band. Else the reach-up stamp; else none. The stamp is not first because it is wrong for the Frays: it
        /// carries the creature's OWN stats up the curve, and attack grows only about 0.05% a level there, so a level 115
        /// Monouga reached 548 attack at 480 (Threads' standard is about 830) and landed about 1 swing in 6 on a level
        /// 441 character.
        /// </summary>
        public static DungeonSpawnPlanEntry EntryFor(bool hasStatCurve, bool hasBandStandard, uint wcid, FraysRole role, int runLevel)
        {
            if (wcid == 0 || runLevel <= 0)
                return null;

            if (hasBandStandard)
                return new DungeonSpawnPlanEntry(wcid, RoleFor(role), null, runLevel, 0);

            return hasStatCurve ? new DungeonSpawnPlanEntry(wcid, RoleFor(role), null, 0, runLevel) : null;
        }

        /// <summary><see cref="EntryFor(bool, bool, uint, FraysRole, int)"/> for a live plan.</summary>
        public static DungeonSpawnPlanEntry EntryFor(DungeonSpawnPlan plan, uint wcid, FraysRole role, int runLevel) =>
            plan == null ? null : EntryFor(plan.StatCurve != null, plan.BandStandard != null && !plan.BandStandard.IsEmpty, wcid, role, runLevel);

        private static readonly ConcurrentDictionary<(string Family, int Level), byte> sampled = new();

        /// <summary>Once per family and level: one shaped creature's numbers in the log, to compare with the old lift.</summary>
        public static void LogSample(Creature creature, string family, int level, DungeonSpawnPlanEntry entry)
        {
            if (creature == null || !sampled.TryAdd((family ?? "", level), 0))
                return;

            try
            {
                uint Skill(ACE.Entity.Enum.Skill s) => creature.GetCreatureSkill(s)?.Current ?? 0;
                var parts = creature.Biota?.PropertiesBodyPart?.Values.Where(p => p != null).ToList();
                var maxDamage = parts == null || parts.Count == 0 ? 0 : parts.Max(p => p.DVal);

                log.Info($"[FRAYS] Threads-shaped sample {family} L{level}: {creature.Name} (wcid {entry?.Wcid}, {entry?.Role}, uplift {entry?.UpliftLevel}, stamp {entry?.StampLevel}) " +
                    $"heavy {Skill(ACE.Entity.Enum.Skill.HeavyWeapons)} light {Skill(ACE.Entity.Enum.Skill.LightWeapons)} finesse {Skill(ACE.Entity.Enum.Skill.FinesseWeapons)} " +
                    $"twohand {Skill(ACE.Entity.Enum.Skill.TwoHandedCombat)} war {Skill(ACE.Entity.Enum.Skill.WarMagic)} body max dmg {maxDamage} health {creature.Health.MaxValue} damage rating {creature.DamageRating}");
            }
            catch (Exception ex)
            {
                log.Warn($"[FRAYS] Threads-shaped sample not logged: {ex.Message}");
            }
        }

        private static readonly ConcurrentDictionary<string, DateTime> rollLogged = new();

        /// <summary>
        /// Diagnostic (for creatures that seem to evade and resist everything): one roll a kind and direction every
        /// 20 seconds, for a fight with a Frays creature on either side, with the numbers the roll used.
        /// </summary>
        public static void LogRoll(string kind, WorldObject attacker, WorldObject defender, double attack, double defense, double missChance, bool missed)
        {
            if ((attacker as Creature)?.FraysTag == null && (defender as Creature)?.FraysTag == null)
                return;

            var key = $"{kind}|{attacker is Player}";
            var now = DateTime.UtcNow;

            if (rollLogged.TryGetValue(key, out var last) && (now - last).TotalSeconds < 20)
                return;

            rollLogged[key] = now;
            log.Info($"[FRAYS] roll {kind}: {attacker?.Name} (L{attacker?.Level}) -> {defender?.Name} (L{defender?.Level}) attack {attack:0} defense {defense:0} miss chance {missChance:P1} {(missed ? "MISSED" : "landed")}");
        }

        /// <summary>Shapes a freshly created creature (before EnterWorld) as its plan entry. False if it threw.</summary>
        public static bool Shape(Creature creature, DungeonSpawnPlan plan, DungeonSpawnPlanEntry entry, string tag)
        {
            if (creature == null || plan == null || entry == null)
                return false;

            try
            {
                ThreadDungeonSpawner.ShapeCreature(creature, plan, entry, 0, new CreatureShapeOptions
                {
                    Run = null,
                    Rewards = false,
                    GroupEffort = GroupScaling.Solo.Effort,
                    LogTag = tag,
                });

                return true;
            }
            catch (Exception ex)
            {
                log.Error($"[FRAYS] {tag}: Threads shaping threw for wcid {entry.Wcid}", ex);
                return false;
            }
        }

        private static DungeonSpawnPlan Build(string family, int level)
        {
            var watch = Stopwatch.StartNew();

            try
            {
                var store = ThreadDungeonManager.Store;
                var tables = WorldEventHooks.Store?.SpeciesTables;

                if (store?.Dungeons == null || store.Dungeons.Count == 0 || tables == null)
                    return null;

                var dungeon = DungeonFor(store);

                if (dungeon == null)
                    return null;

                var tier = Math.Clamp(FraysRules.TierForLevel(FraysSettings.Text(FraysSettings.LootTierByLevel), level), 1, DungeonGemSpec.MaxTier);
                var spec = new DungeonGemSpec(dungeon.Id, level, tier, family, unchecked(family.GetHashCode() ^ level), null, 0u, 0u);

                // The archetype and per-hit reads give the plan Threads' damage ceiling (its per-hit median). Without them the
                // median is 0 and the ceiling does nothing, so a band-uplifted Monouga kept 2716 body damage at 480 and killed
                // a level 443 character in one or two hits.
                var strip = SpellStripConfig.FromPropertyManager();

                var plan = DungeonPopulationBuilder.Build(spec, dungeon, store, tables,
                    ThreadDungeonSpawner.LiveLevelOf, ThreadDungeonSpawner.LiveHealthOf, ThreadDungeonSpawner.ReadLimits(GroupScaling.Solo),
                    new Random(spec.Seed), ThreadDungeonSpawner.LiveProfileOf,
                    archetypeProfileOf: w => ThreadDungeonSpawner.LiveArchetypeProfileOf(w, strip),
                    perHitOf: w => ThreadDungeonSpawner.LivePerHitOf(w, strip));

                var band = plan?.BandStandard != null && !plan.BandStandard.IsEmpty;

                if (plan == null || (plan.StatCurve == null && !band))
                {
                    log.Warn($"[FRAYS] Threads plan for {family} at level {level}: no stat curve or band standard; the Frays' own lift is used");
                    return null;
                }

                log.Info($"[FRAYS] Threads plan for {family} at level {level} (tier {tier}, {dungeon.Id}): {plan.Entries.Count(e => e.Role == DungeonRole.Trash)} trash, {plan.Entries.Count(e => e.Role == DungeonRole.Elite)} elite, " +
                    $"stat curve {(plan.StatCurve != null ? "yes" : "no")}, band standard {(band ? plan.BandStandard.SampleCount + " members" : "none")}, " +
                    $"damage ceiling {(plan.PerHitMedian > 0 ? $"median {plan.PerHitMedian:0} x{plan.DamageCeilingCap:0.##}" : "off")}, in {watch.ElapsedMilliseconds} ms");
                return plan;
            }
            catch (Exception ex)
            {
                // The builder refuses a forced family with no trash in the level's band: that family keeps the Frays' own lift.
                log.Warn($"[FRAYS] Threads plan for {family} at level {level} not built ({ex.Message}); the Frays' own lift is used");
                return null;
            }
        }

        /// <summary>The Threads dungeon the plans are built against: the one with the most curated spawn points (more entries to draw from).</summary>
        private static ACE.Server.ThreadDungeons.Defs.DungeonEntryDef DungeonFor(ThreadDungeonStore store)
        {
            if (dungeonId != null && store.Dungeons.TryGetValue(dungeonId, out var known) && known != null)
                return known;

            var best = store.Dungeons.Values.Where(d => d?.Points != null)
                .OrderByDescending(d => d.Points.Count(p => p != null && p.Curated))
                .ThenBy(d => d.Id, StringComparer.Ordinal)
                .FirstOrDefault();

            dungeonId = best?.Id;
            return best;
        }
    }
}
