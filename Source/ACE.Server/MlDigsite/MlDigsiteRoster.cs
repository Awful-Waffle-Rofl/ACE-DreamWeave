using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using ACE.Server.MlTreasure;

using log4net;

namespace ACE.Server.MlDigsite
{
    /// <summary>One creature a digsite encounter may draw, with the level its weenie was authored on.</summary>
    public readonly struct MlDigsiteRosterEntry
    {
        public MlDigsiteRosterEntry(uint wcid, int level, string name)
        {
            Wcid = wcid;
            Level = level;
            Name = name;
        }

        public uint Wcid { get; }
        public int Level { get; }
        public string Name { get; }
    }

    /// <summary>
    /// Which creatures a digsite encounter spawns, by role.
    ///
    /// EVERY WCID HERE IS AN ALREADY-SHIPPED MARAE LASSEL BESTIARY WEENIE. Nothing in this file authors a
    /// creature; it only decides which of the island's own monsters a dug-up fight draws from, which is what
    /// keeps a digsite encounter looking like the island it happens on rather than like a Thread.
    /// Cross-checked against Content/sql/weenies (the files exist) and Docs/Marae-Lassel/BESTIARY.md
    /// sections 5a-5c and 5e (which is the authority for level and role).
    ///
    /// THIS TABLE IS A DESIGN DEFAULT, NOT A DERIVED FACT. The encounter design settled the shapes, the
    /// pacing and the rewards but never named a roster, so the bands below are a judgement call: the
    /// BESTIARY's own "pack / swarm / skirmisher" entries became Wave, its "caster / elite caster" entries
    /// became Priority, its "heavy / bruiser / soldier caste" entries became MiniBoss, and its six apex
    /// named units became Boss. It is deliberately one table in one file so retuning it is a single edit.
    ///
    /// Two deliberate exclusions, both load-bearing:
    ///   * wcid 1002871 "Drumtaken Armored Tusker" is CUT FROM THE ISLAND (BESTIARY.md section 5b, round 11,
    ///     2026-09-15 owner ruling). Its weenie and camp generator are kept defined but unreferenced, so
    ///     drawing it here would put a creature back into the world that an owner ruling took out of it.
    ///   * the Aun warband (1002892-1002906) and the Hivebound additions are left out because they are
    ///     faction units belonging to their own arc's escalation, not ambient wildlife a hole in the ground
    ///     should produce.
    /// </summary>
    public static class MlDigsiteRoster
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// Wave trash: the island's pack, swarm and skirmisher roles across all three zones, so a wave reads
        /// as "the local wildlife arrived" at whichever end of the island the site rolled on.
        /// </summary>
        private static readonly MlDigsiteRosterEntry[] WaveEntries =
        {
            // South zone, rung 185
            new MlDigsiteRosterEntry(1002840, 185, "Drumtaken Littoral Siraluun"),
            new MlDigsiteRosterEntry(1002841, 185, "Drumtaken Marsh Siraluun"),
            new MlDigsiteRosterEntry(1003673, 185, "Drumtaken Tidal Siraluun"),
            new MlDigsiteRosterEntry(1002842, 185, "Drumtaken Carenzi Trampler"),
            new MlDigsiteRosterEntry(1002843, 185, "Drumtaken Carenzi Sentry"),
            new MlDigsiteRosterEntry(1002844, 185, "Drumtaken Carenzi Stalker"),
            new MlDigsiteRosterEntry(1002846, 185, "Drumtaken Sable Gromnie"),
            new MlDigsiteRosterEntry(1002847, 185, "Olthoi Drone"),

            // North zone, rung 215
            new MlDigsiteRosterEntry(1002856, 215, "Drumtaken Kithless Siraluun"),
            new MlDigsiteRosterEntry(1002857, 215, "Drumtaken Timber Siraluun"),
            new MlDigsiteRosterEntry(1002858, 215, "Drumtaken Rabid Carenzi"),
            new MlDigsiteRosterEntry(1002859, 215, "Drumtaken Feral Carenzi"),
            new MlDigsiteRosterEntry(1002862, 215, "Drumtaken Ebon Gromnie"),
            new MlDigsiteRosterEntry(1002863, 215, "Drumtaken Copper Gromnie"),
            new MlDigsiteRosterEntry(1002864, 215, "Drumtaken Canescent Mattekar"),
            new MlDigsiteRosterEntry(1002865, 215, "Olthoi Piercer"),

            // Plateau zone, rung 240
            new MlDigsiteRosterEntry(1002876, 240, "Drumtaken Untamed Siraluun"),
            new MlDigsiteRosterEntry(1002877, 240, "Drumtaken Badlands Siraluun"),
            new MlDigsiteRosterEntry(1002878, 240, "Drumtaken Savage Carenzi"),
            new MlDigsiteRosterEntry(1002879, 240, "Drumtaken Carnivorous Carenzi"),
            new MlDigsiteRosterEntry(1002881, 240, "Drumtaken Brass Gromnie"),
            new MlDigsiteRosterEntry(1002883, 240, "Olthoi Lancer"),
        };

        /// <summary>
        /// The Corruption encounter's priority mob: the island's casters. A caster is the right shape for
        /// "kill this one first" - it is the entry a group must break off to reach, rather than something
        /// they would have killed incidentally while clearing the field around it.
        /// </summary>
        private static readonly MlDigsiteRosterEntry[] PriorityEntries =
        {
            new MlDigsiteRosterEntry(1002845, 185, "Siberite Fragment"),
            new MlDigsiteRosterEntry(1003654, 200, "Drumtaken Kirit Zefir"),
            new MlDigsiteRosterEntry(1002860, 215, "Siberite Shard"),
            new MlDigsiteRosterEntry(1002861, 215, "Drumtaken Azael Zefir"),
            new MlDigsiteRosterEntry(1002880, 240, "Drumtaken Enku Zefir"),
            new MlDigsiteRosterEntry(1002882, 240, "Virindi Observer"),
            new MlDigsiteRosterEntry(1002888, 265, "Virindi Executor"),
        };

        /// <summary>
        /// The Waves encounter's closing mini-boss: the island's heavies, bruisers and Olthoi soldier
        /// castes. Harder than anything in the three waves that preceded it, and short of the named apex
        /// units, which are reserved for Boss Rush.
        /// </summary>
        private static readonly MlDigsiteRosterEntry[] MiniBossEntries =
        {
            new MlDigsiteRosterEntry(1002848, 200, "Drumtaken Strand Siraluun"),
            new MlDigsiteRosterEntry(1002849, 200, "Drumtaken Sand Golem"),
            new MlDigsiteRosterEntry(1002850, 200, "Drumtaken Coral Golem"),
            new MlDigsiteRosterEntry(1002851, 200, "Drumtaken Marae Ursuin"),
            new MlDigsiteRosterEntry(1002852, 200, "Olthoi Servant"),
            new MlDigsiteRosterEntry(1002866, 220, "Drumtaken Elariwood Golem"),
            new MlDigsiteRosterEntry(1002867, 220, "Drumtaken Basalt Golem"),
            new MlDigsiteRosterEntry(1002868, 220, "Drumtaken Ironstone Golem"),
            new MlDigsiteRosterEntry(1002869, 220, "Drumtaken Ferocious Ursuin"),
            new MlDigsiteRosterEntry(1002870, 220, "Drumtaken Woodland Ursuin"),
            new MlDigsiteRosterEntry(1002872, 220, "Olthoi Soldier"),
            new MlDigsiteRosterEntry(1002873, 220, "Olthoi Eviscerator Grub"),
            new MlDigsiteRosterEntry(1002874, 220, "Olthoi Legionary"),
            new MlDigsiteRosterEntry(1002884, 265, "Drumtaken Verdigris Golem"),
            new MlDigsiteRosterEntry(1002885, 265, "Drumtaken Granite Golem"),
            new MlDigsiteRosterEntry(1002886, 265, "Drumtaken Ursuin Slicer"),
            new MlDigsiteRosterEntry(1002887, 265, "Drumtaken Raging Ursuin"),
            new MlDigsiteRosterEntry(1002889, 265, "Olthoi Warrior"),
            new MlDigsiteRosterEntry(1002890, 265, "Olthoi Hive Mutilator"),
        };

        /// <summary>
        /// Boss Rush: the island's six named apex units. These are the hardest single targets Marae Lassel
        /// has, which is what makes the 7-weight branch feel like the rare one it is.
        ///
        /// Aun Relaria the Unburied (1004120) is deliberately NOT here. It is the boss-variant MAP's own
        /// creature and stays exclusive to that path - putting it in this table would make the rarest thing
        /// in the feature reachable two ways.
        /// </summary>
        private static readonly MlDigsiteRosterEntry[] BossEntries =
        {
            new MlDigsiteRosterEntry(1002853, 205, "Rauhea the Longwing"),
            new MlDigsiteRosterEntry(1002854, 205, "Tanepo the Nine-Scarred"),
            new MlDigsiteRosterEntry(1002855, 205, "Rangi of the Drowned Rank"),
            new MlDigsiteRosterEntry(1002875, 225, "Whakaruru the Hivekeeper"),
            new MlDigsiteRosterEntry(1002891, 275, "Hauwhenua the Sky-Torn"),
            new MlDigsiteRosterEntry(1003672, 275, "Tureia the Underbeat"),
        };

        /// <summary>
        /// The rare Kept Siraluun miniboss, one of eight already-shipped weenies (wcids 1005470-1005477,
        /// Content/sql/weenies) that substitute for an ordinary mini-boss or boss draw at
        /// ml_digsite_kept_siraluun_chance. Each keeps the authored Level (PropertyInt 25) of its own base
        /// variant unchanged - see each weenie's own SQL header for "Base variant" - which is what lets
        /// MlDigsiteRules.PickKeptSiraluun prefer the one whose Level matches the roll it is replacing.
        ///
        /// None of these eight is referenced by any roster ABOVE - every one of their weenie files says so
        /// itself ("NOT wired into any digsite roster or spawner by this unit"). This table is that wiring.
        /// </summary>
        private static readonly MlDigsiteRosterEntry[] KeptSiraluunEntries =
        {
            new MlDigsiteRosterEntry(1005470, 215, "Kept Kithless Siraluun"),
            new MlDigsiteRosterEntry(1005471, 240, "Kept Badlands Siraluun"),
            new MlDigsiteRosterEntry(1005472, 185, "Kept Littoral Siraluun"),
            new MlDigsiteRosterEntry(1005473, 185, "Kept Marsh Siraluun"),
            new MlDigsiteRosterEntry(1005474, 200, "Kept Strand Siraluun"),
            new MlDigsiteRosterEntry(1005475, 185, "Kept Tidal Siraluun"),
            new MlDigsiteRosterEntry(1005476, 215, "Kept Timber Siraluun"),
            new MlDigsiteRosterEntry(1005477, 240, "Kept Untamed Siraluun"),
        };

        /// <summary>
        /// The Boss Rush mechanic add: ONE entry, by owner ruling (2026-09-20) - "volatile adds use one
        /// reused add weenie whose health and speed scale per set, not a distinct weenie per set. Cheapest to
        /// tune and one thing to balance." The per-set scaling lives in ml_digsite_bossrush_sets' addhp and
        /// addspeed tokens, applied at spawn by MlDigsiteSpawner, so this table never needs a second row to
        /// make a set's adds faster or frailer.
        ///
        /// wcid 1002844 "Drumtaken Carenzi Stalker" is already in <see cref="WaveEntries"/> and is deliberately
        /// the same creature: a Carenzi stalker is the island's small fast pack animal, which is exactly the
        /// read the tester asked for ("fast, weak adds chase individual players"), and reusing a shipped
        /// bestiary weenie keeps the no-new-creature rule this whole roster is built on. The band is separate
        /// from WaveEntries rather than pointing at it because an add is drawn for a different purpose and
        /// must stay retunable without moving wave trash.
        ///
        /// ZONE ISOLATION EXEMPTION: this owner ruling is ONE weapon across every set and zone by design, so
        /// <see cref="MlDigsiteRole.Add"/> is deliberately EXEMPT from the zone filter in <see cref="Pick"/> -
        /// it is never widened by the empty-band fallback, because it is never filtered in the first place.
        /// This is safe rather than a hole: a Boss Rush add is always the WEAK, FAST South-band stalker,
        /// scaled down further by the mechanic set's own addhp/addspeed tokens (MlDigsiteSpawner.ApplyAddScaling),
        /// so a South-band add can never make a North/Plateau fight harder than its own set already intends.
        /// </summary>
        private static readonly MlDigsiteRosterEntry[] AddEntries =
        {
            new MlDigsiteRosterEntry(1002844, 185, "Drumtaken Carenzi Stalker"),
        };

        /// <summary>Every Kept Siraluun entry, in table order. Never empty.</summary>
        public static IReadOnlyList<MlDigsiteRosterEntry> KeptSiraluun => KeptSiraluunEntries;

        /// <summary>The wcid the BESTIARY's round-11 ruling took off the island. Never drawn; asserted by the tests.</summary>
        public const uint CutFromTheIslandWcid = 1002871;

        /// <summary>Every entry for a role, in table order. Never null, never empty for a shipped role.</summary>
        public static IReadOnlyList<MlDigsiteRosterEntry> Entries(MlDigsiteRole role)
        {
            switch (role)
            {
                case MlDigsiteRole.Wave: return WaveEntries;
                case MlDigsiteRole.Priority: return PriorityEntries;
                case MlDigsiteRole.MiniBoss: return MiniBossEntries;
                case MlDigsiteRole.Checkpoint: return MiniBossEntries;
                case MlDigsiteRole.Boss: return BossEntries;
                case MlDigsiteRole.Add: return AddEntries;
                default: return Array.Empty<MlDigsiteRosterEntry>();
            }
        }

        /// <summary>
        /// One uniform draw from a role's band, filtered to <paramref name="zone"/> when it is not Unknown -
        /// zone isolation (owner-approved design): an entry qualifies when its own authored Level falls in
        /// that zone's band (<see cref="MlTreasureZones.FromLevel"/>). Unknown applies no filter at all,
        /// exactly the pre-zone-isolation draw.
        ///
        /// <see cref="MlDigsiteRole.Add"/> is EXEMPT from the zone filter entirely (see the ruling quoted on
        /// <see cref="AddEntries"/>): it is always drawn from its full, unfiltered band regardless of zone,
        /// and never triggers the empty-band fallback below, because it is never filtered in the first place.
        ///
        /// For every OTHER role, a role/zone combination with NO qualifying entries falls back to the role's
        /// FULL (unfiltered) band rather than refusing the spawn, and logs a warning the first time that
        /// happens for this (role, zone) pair - never again, so a draw in a thin zone does not spam the log
        /// every spawn. This never invents a roster entry: it only widens which of the role's EXISTING
        /// entries may be drawn. This branch is defensive: every shipped role except Add currently has at
        /// least one entry in every zone (MlTreasureZoneTests), so it is unreached by real content today -
        /// it stays in place, and covered by test, for the day a role's band does not.
        /// </summary>
        public static MlDigsiteRosterEntry? Pick(MlDigsiteRole role, MlTreasureZone zone, Random rng)
        {
            var entries = Entries(role);

            if (entries.Count == 0)
                return null;

            rng = rng ?? new Random();

            var pool = SelectPool(entries, role, zone);

            return pool[rng.Next(pool.Count)];
        }

        /// <summary>
        /// The pool <see cref="Pick"/> draws from: the zone filter plus its empty-band fallback, as pure
        /// logic over an arbitrary entry list - a seam so the fallback branch (unreachable by any shipped
        /// role's real table today, per the class remarks) can still be exercised directly by test with a
        /// synthetic entries list, without needing a real role whose band is actually thin in some zone.
        /// </summary>
        internal static IReadOnlyList<MlDigsiteRosterEntry> SelectPool(IReadOnlyList<MlDigsiteRosterEntry> entries, MlDigsiteRole role, MlTreasureZone zone)
        {
            if (zone == MlTreasureZone.Unknown || role == MlDigsiteRole.Add)
                return entries;

            var filtered = entries.Where(e => MlTreasureZones.FromLevel(e.Level) == zone).ToList();

            if (filtered.Count > 0)
                return filtered;

            if (fallbackWarned.TryAdd((role, zone), 0))
                log.Warn($"[ML_DIGSITE] roster role {role} has no entry in zone {zone}; falling back to the full band");

            return entries;
        }

        /// <summary>Backward-compatible unfiltered draw - equivalent to <see cref="Pick(MlDigsiteRole, MlTreasureZone, Random)"/>
        /// with zone Unknown.</summary>
        public static MlDigsiteRosterEntry? Pick(MlDigsiteRole role, Random rng) => Pick(role, MlTreasureZone.Unknown, rng);

        /// <summary>Latches the "no entry in zone" warning to once per (role, zone) pair for the process
        /// lifetime.</summary>
        private static readonly ConcurrentDictionary<(MlDigsiteRole, MlTreasureZone), byte> fallbackWarned = new();

        /// <summary>Every wcid this roster can ever spawn, across all roles. Used by the tests and by logging.</summary>
        public static IReadOnlyList<uint> AllWcids()
        {
            return Enum.GetValues(typeof(MlDigsiteRole))
                .Cast<MlDigsiteRole>()
                .SelectMany(Entries)
                .Select(e => e.Wcid)
                .Distinct()
                .OrderBy(w => w)
                .ToList();
        }
    }
}
