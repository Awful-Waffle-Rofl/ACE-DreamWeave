using System;
using System.Collections.Generic;

using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities
{
    /// <summary>
    /// The Spellsword class's damage-type -> war-spell ladders, plus the shared "how high a rung can this
    /// character throw" selector. Every id here is transcribed from SPELLSWORD-DESIGN.md section 2, which
    /// read them out of the live ace_world.spell table on 2026-08-03 and cross-checked them against
    /// ACE.Entity.Enum.SpellId; the enum member names below were then resolved back from those numeric ids,
    /// so the two independent sources agree on every rung. Nothing here is derived from an id offset - see
    /// the Ring table for why that matters.
    ///
    /// WHY THIS IS A SEPARATE STATIC CLASS. Four handlers (Spellblade, Runeblade, Spellstorm, Sundermark)
    /// need the same damage-type dispatch and the same skill-to-level rule. Keeping it here makes the rule
    /// testable without constructing a Player, and makes "which rung did we pick" a pure function of
    /// (ladder, skill, rank, scale) rather than something buried in four copies of a proc handler.
    /// </summary>
    public static class SpellswordSpellTables
    {
        // ------------------------------------------------------------------------------------------------
        // Rung levels
        // ------------------------------------------------------------------------------------------------

        /// <summary>
        /// The actual SPELL LEVEL of each rung of the Streak ladder, parallel to every array in
        /// <see cref="StreakByType"/>.
        ///
        /// The Streak ladder IS contiguous 1-8, but do not take that on trust from the spell NAMES - level 7
        /// breaks the roman-numeral convention entirely. There is no "Flame Streak VII": the level-7 rung is
        /// a uniquely named spell per element (Sizzling Fury, Corrosive Flash, Rending Wind, Outlander's
        /// Insolence, Cameron's Curse, Sudden Frost, Lhen's Flare), exactly the way the level-7 vulnerability
        /// rung is the "X's Gift" family. The SpellId enum does carry them as ...Streak7 members.
        ///
        /// This is the same trap the vulnerability ladder sprang on ElementalRendAbility, which shipped
        /// clamped to 6 rungs on a comment asserting the ladder ended there. Verified here against
        /// portal.dat: the level-7 streaks report Power 300, which is MinPower[7], and their damage
        /// (42/42) sits exactly between level VI's 36/35 and the Incantation's 47/47. Verified they belong
        /// to the STREAK line rather than the Bolt line by that damage progression - Flame Bolt VI is
        /// 84/84, so 42/42 cannot continue it. The BOLT ladder does have a level 7 (Flame Bolt VII is
        /// Ilservian's Flame, 115/74, id 2128), under the same uniquely-named convention; see
        /// <see cref="BoltLevels"/>.
        /// </summary>
        public static readonly uint[] StreakLevels = { 1, 2, 3, 4, 5, 6, 7, 8 };

        /// <summary>
        /// The actual SPELL LEVEL of each rung of the Blast ladder, parallel to every array in
        /// <see cref="BlastByType"/>. Blast has no level I or II at all (design section 2b: "Starts at III.
        /// There is no Blast I or II"), but it DOES have a level 7, under the same uniquely-named convention
        /// as the Streaks (Dissolving Vortex, Silencia's Scorn, Sau Kolin's Sword, Stinging Needles,
        /// Pummeling Storm, Winter's Embrace, Luminous Wrath). Hence {3,4,5,6,7,8} - the ladder skips only
        /// the bottom.
        ///
        /// The level-7 blasts report portal.dat Power 325, not 300; both fall in level 7's band, since
        /// MinPower[7] is 300 and MinPower[8] is 400. They carry 5 projectiles like the Incantation rather
        /// than the 3 of levels III-VI, and are distinguished from the 5-projectile VOLLEY family of the
        /// same tier by spread_Angle: Blast is 90 degrees, Volley is 0.
        /// </summary>
        public static readonly uint[] BlastLevels = { 3, 4, 5, 6, 7, 8 };

        /// <summary>
        /// The tier-I Ring block is a single rung, and it is level 5 (design section 2c). Declared as an
        /// array so it can be fed to the same selector as the other two ladders if a rank ladder is ever
        /// added; today Spellstorm is a single-rank entry and simply fires <see cref="RingByType"/>.
        /// </summary>
        public static readonly uint[] RingLevels = { 5 };

        /// <summary>
        /// The actual SPELL LEVEL of each rung of the Bolt ladder, parallel to every array in
        /// <see cref="BoltByType"/>. Contiguous 1-8 for all eight types: level 7 is the uniquely-named rung
        /// (Disintegration, Ilservian's Flame, The Spike, Icy Torment, Alset's Coil, Crushing Shame,
        /// Evisceration; Nether Bolt VII is named normally) and level 8 is the Incantation. Verified against
        /// ace_world.spell on 2026-11-01: every one of the 64 rows exists, including all eight Nether Bolts
        /// (5349-5356). Bolt shares the Streak's level numbering, so the SAME rung index selects the SAME
        /// level on both ladders - which is what makes "Streak becomes Bolt at the same rung" a table swap.
        /// Level 8 is unreachable from a proc anyway (<see cref="MaxLevelForRank"/> caps at 7).
        /// </summary>
        public static readonly uint[] BoltLevels = { 1, 2, 3, 4, 5, 6, 7, 8 };

        /// <summary>
        /// The Vulnerability ladder's rung levels. Unlike the war families this one IS contiguous 1-8: the
        /// level-7 rung exists as the "X's Gift" family (Swordsman's Gift, Olthoi's Gift, ...) and level 8
        /// as "Incantation of X Vulnerability Other", so every level from 1 to 8 has a real spell. Used by
        /// Sundermark, which reads the ids themselves from ElementalRendAbility.GetVulnerabilitySpell
        /// rather than duplicating that table here.
        /// </summary>
        public static readonly uint[] VulnerabilityLevels = { 1, 2, 3, 4, 5, 6, 7, 8 };

        // ------------------------------------------------------------------------------------------------
        // Ladders
        // ------------------------------------------------------------------------------------------------

        /// <summary>
        /// Streak - the T1 game-changer (Spellblade). Complete for all seven physical/elemental types, which
        /// is exactly why the design picked it for tier 1: no damage type has to fall back to another.
        ///
        /// SPELLSWORD-DESIGN.md section 2a, transcribed row by row. Eight rungs: levels I-VI, then the
        /// uniquely-named level VII (2121-2147, e.g. Corrosive Flash for Acid), then the level-VIII
        /// Incantation - see <see cref="StreakLevels"/>:
        ///
        ///   Slash    (Whirling Blade) 1826 1827 1828 1829 1830 1831 2147 4458
        ///   Pierce   (Force)          1802 1803 1804 1805 1806 1807 2133 4444
        ///   Bludgeon (Shock Wave)     1820 1821 1822 1823 1824 1825 2145 4456
        ///   Cold     (Frost)          1808 1809 1810 1811 1812 1813 2137 4448
        ///   Fire     (Flame)          1796 1797 1798 1799 1800 1801 2129 4440
        ///   Acid                      1790 1791 1792 1793 1794 1795 2121 4432
        ///   Electric (Lightning)      1814 1815 1816 1817 1818 1819 2141 4452
        /// </summary>
        public static readonly IReadOnlyDictionary<DamageType, SpellId[]> StreakByType = new Dictionary<DamageType, SpellId[]>
        {
            [DamageType.Slash] = new[]
            {
                SpellId.WhirlingBladeStreak1, SpellId.WhirlingBladeStreak2, SpellId.WhirlingBladeStreak3,
                SpellId.WhirlingBladeStreak4, SpellId.WhirlingBladeStreak5, SpellId.WhirlingBladeStreak6,
                SpellId.WhirlingBladeStreak7, SpellId.WhirlingBladeStreak8,
            },
            [DamageType.Pierce] = new[]
            {
                SpellId.ForceStreak1, SpellId.ForceStreak2, SpellId.ForceStreak3,
                SpellId.ForceStreak4, SpellId.ForceStreak5, SpellId.ForceStreak6,
                SpellId.ForceStreak7, SpellId.ForceStreak8,
            },
            [DamageType.Bludgeon] = new[]
            {
                SpellId.ShockwaveStreak1, SpellId.ShockwaveStreak2, SpellId.ShockwaveStreak3,
                SpellId.ShockwaveStreak4, SpellId.ShockwaveStreak5, SpellId.ShockwaveStreak6,
                SpellId.ShockwaveStreak7, SpellId.ShockwaveStreak8,
            },
            [DamageType.Cold] = new[]
            {
                SpellId.FrostStreak1, SpellId.FrostStreak2, SpellId.FrostStreak3,
                SpellId.FrostStreak4, SpellId.FrostStreak5, SpellId.FrostStreak6,
                SpellId.FrostStreak7, SpellId.FrostStreak8,
            },
            [DamageType.Fire] = new[]
            {
                SpellId.FlameStreak1, SpellId.FlameStreak2, SpellId.FlameStreak3,
                SpellId.FlameStreak4, SpellId.FlameStreak5, SpellId.FlameStreak6,
                SpellId.FlameStreak7, SpellId.FlameStreak8,
            },
            [DamageType.Acid] = new[]
            {
                SpellId.AcidStreak1, SpellId.AcidStreak2, SpellId.AcidStreak3,
                SpellId.AcidStreak4, SpellId.AcidStreak5, SpellId.AcidStreak6,
                SpellId.AcidStreak7, SpellId.AcidStreak8,
            },
            [DamageType.Electric] = new[]
            {
                SpellId.LightningStreak1, SpellId.LightningStreak2, SpellId.LightningStreak3,
                SpellId.LightningStreak4, SpellId.LightningStreak5, SpellId.LightningStreak6,
                SpellId.LightningStreak7, SpellId.LightningStreak8,
            },
            // Nether (Void Magic), owner ruling 2026-10-27: a Nether weapon procs the void ladder. THE IDS ARE
            // NOT CONTIGUOUS: levels I-IV are 5357-5360 and levels V-VIII are 5345-5348 (the Nether Bolt
            // block 5349-5356 sits between them). Resolved from the SpellId enum, not from an offset.
            [DamageType.Nether] = new[]
            {
                SpellId.NetherStreak1, SpellId.NetherStreak2, SpellId.NetherStreak3,
                SpellId.NetherStreak4, SpellId.NetherStreak5, SpellId.NetherStreak6,
                SpellId.NetherStreak7, SpellId.NetherStreak8,
            },
        };

        /// <summary>
        /// Blast - the T2 game-changer (Runeblade). A 90 degree cone: one projectile lands on the centre
        /// target and the rest impact-check other monsters, so Blast's single-target damage equals Streak's
        /// at the same level and everything it adds over Spellblade is cleave (design section 2b / Q1).
        ///
        /// SPELLSWORD-DESIGN.md section 2b, transcribed row by row. THE LADDER STARTS AT LEVEL III - there
        /// is no Blast I or II - and its top rung is the level-VIII Incantation, so a Blast array has five
        /// entries where a Streak array has seven. See <see cref="BlastLevels"/>.
        ///
        ///   Slash    (Blade)      123 124 125 126 2124 4435
        ///   Pierce   (Force)      119 120 121 122 2131 4442
        ///   Bludgeon (Shock)      103 104 105 106 2143 4454
        ///   Cold     (Frost)      107 108 109 110 2135 4446
        ///   Fire     (Flame)      115 116 117 118 2127 4438
        ///   Acid                   99 100 101 102 2120 4431
        ///   Electric (Lightning)  111 112 113 114 2139 4450
        ///
        /// Several Blast rows have exact-duplicate spell rows in the world DB (Acid Blast III exists at both
        /// 99 and 3653); the design ruled that the LOWEST id is the canonical player-facing row, which is
        /// what the enum members below resolve to.
        /// </summary>
        public static readonly IReadOnlyDictionary<DamageType, SpellId[]> BlastByType = new Dictionary<DamageType, SpellId[]>
        {
            [DamageType.Slash] = new[]
            {
                SpellId.BladeBlast3, SpellId.BladeBlast4, SpellId.BladeBlast5, SpellId.BladeBlast6,
                SpellId.BladeBlast7, SpellId.BladeBlast8,
            },
            [DamageType.Pierce] = new[]
            {
                SpellId.ForceBlast3, SpellId.ForceBlast4, SpellId.ForceBlast5, SpellId.ForceBlast6,
                SpellId.ForceBlast7, SpellId.ForceBlast8,
            },
            [DamageType.Bludgeon] = new[]
            {
                SpellId.ShockBlast3, SpellId.ShockBlast4, SpellId.ShockBlast5, SpellId.ShockBlast6,
                SpellId.ShockBlast7, SpellId.ShockBlast8,
            },
            [DamageType.Cold] = new[]
            {
                SpellId.FrostBlast3, SpellId.FrostBlast4, SpellId.FrostBlast5, SpellId.FrostBlast6,
                SpellId.FrostBlast7, SpellId.FrostBlast8,
            },
            [DamageType.Fire] = new[]
            {
                SpellId.FlameBlast3, SpellId.FlameBlast4, SpellId.FlameBlast5, SpellId.FlameBlast6,
                SpellId.FlameBlast7, SpellId.FlameBlast8,
            },
            [DamageType.Acid] = new[]
            {
                SpellId.AcidBlast3, SpellId.AcidBlast4, SpellId.AcidBlast5, SpellId.AcidBlast6,
                SpellId.AcidBlast7, SpellId.AcidBlast8,
            },
            [DamageType.Electric] = new[]
            {
                SpellId.LightningBlast3, SpellId.LightningBlast4, SpellId.LightningBlast5, SpellId.LightningBlast6,
                SpellId.LightningBlast7, SpellId.LightningBlast8,
            },
            // Nether (Void Magic), 5546-5551: Nether Blast III-VI, then VII and the Incantation. Same shape as
            // the elemental Blasts - 3 projectiles at III-VI, 5 at VII-VIII, 90 degree spread.
            [DamageType.Nether] = new[]
            {
                SpellId.NetherBlast3, SpellId.NetherBlast4, SpellId.NetherBlast5, SpellId.NetherBlast6,
                SpellId.NetherBlast7, SpellId.NetherBlast8,
            },
        };

        /// <summary>
        /// Ring - the T3 game-changer (Spellstorm). Nine projectiles at spread angle 360, radiating outward,
        /// so one target takes roughly one projectile and a surrounding pack takes one each. ONE fixed rung
        /// per damage type: the user's ruling is that Spellstorm fires only the tier-I ring, which is a
        /// shape change rather than a ladder, and the tier-I block is stat-identical (42/42, avg 63) across
        /// all seven types, so no normalization is needed.
        ///
        /// SPELLSWORD-DESIGN.md section 2c. THE IDS ARE NOT IN DAMAGE-TYPE ORDER, and the design says so
        /// explicitly: "the tier-I ids are not in damage-type order (Acid 1783, Slash 1784, Fire 1785,
        /// Pierce 1786, Cold 1787, Electric 1788, Bludgeon 1789), so the lookup table must be written out,
        /// not computed from an offset". Each row below pairs the design's thematic name with the id and the
        /// enum member that id resolves to:
        ///
        ///   Acid     Searing Disc            1783 -> SpellId.AcidRing
        ///   Slash    Horizon's Blades        1784 -> SpellId.BladeRing
        ///   Fire     Cassius' Ring of Fire   1785 -> SpellId.FlameRing
        ///   Pierce   Nuhmudira's Spines      1786 -> SpellId.ForceRing
        ///   Cold     Halo of Frost           1787 -> SpellId.FrostRing
        ///   Electric Eye of the Storm        1788 -> SpellId.LightningRing
        ///   Bludgeon Tectonic Rifts          1789 -> SpellId.ShockwaveRing
        ///
        /// Do not go looking for these by name: the family is named thematically, so a search for "Ring"
        /// finds neither Tectonic Rifts nor Halo of Frost, and the lesser "X Ring" rows (3805-3808) it does
        /// find are a different, incomplete set.
        /// </summary>
        public static readonly IReadOnlyDictionary<DamageType, SpellId> RingByType = new Dictionary<DamageType, SpellId>
        {
            [DamageType.Acid]     = SpellId.AcidRing,
            [DamageType.Slash]    = SpellId.BladeRing,
            [DamageType.Fire]     = SpellId.FlameRing,
            [DamageType.Pierce]   = SpellId.ForceRing,
            [DamageType.Cold]     = SpellId.FrostRing,
            [DamageType.Electric] = SpellId.LightningRing,
            [DamageType.Bludgeon] = SpellId.ShockwaveRing,
            // Nether: Clouded Soul, 5361 - 9 projectiles at 360 degrees like the tier-I elemental rings, but
            // NOT stat-identical to them (94/53, avg 120.5 against 42/42, avg 63). The proc damage is brought
            // back to the Fire ring's average by NetherParityScale - see that method.
            [DamageType.Nether]   = SpellId.NetherRing,
        };

        // ------------------------------------------------------------------------------------------------
        // Full-power upgrades (2026-11-01) - see SPELLSWORD-DESIGN.md section 11
        // ------------------------------------------------------------------------------------------------

        /// <summary>
        /// Bolt - what a Spellblade proc casts INSTEAD of its Streak on a swing at a full power bar. Same
        /// rung index as <see cref="StreakByType"/> (both ladders are levels 1-8), so the level selection,
        /// rank cap and skill rule are untouched; only the shape changes. A Bolt averages about 2.35x a Streak
        /// of the same level. Every id was read out of ace_world.spell AND resolved from the SpellId enum
        /// (never from an offset) on 2026-11-01:
        ///
        ///   Slash    (Whirling Blade) 92 93 94 95 96 97 2146 4457
        ///   Pierce   (Force Bolt)     86 87 88 89 90 91 2132 4443
        ///   Bludgeon (Shock Wave)     64 65 66 67 68 69 2144 4455
        ///   Cold     (Frost Bolt)     28 70 71 72 73 74 2136 4447
        ///   Fire     (Flame Bolt)     27 81 82 83 84 85 2128 4439
        ///   Acid     (Acid Stream)    58 59 60 61 62 63 2122 4433
        ///   Electric (Lightning Bolt) 75 76 77 78 79 80 2140 4451
        ///   Nether   (Nether Bolt)    5349-5356
        ///
        /// The level-7 rungs are the uniquely-named spells (Evisceration, The Spike, Crushing Shame, Icy
        /// Torment, Ilservian's Flame, Disintegration, Alset's Coil).
        /// </summary>
        public static readonly IReadOnlyDictionary<DamageType, SpellId[]> BoltByType = new Dictionary<DamageType, SpellId[]>
        {
            [DamageType.Slash] = new[]
            {
                SpellId.WhirlingBlade1, SpellId.WhirlingBlade2, SpellId.WhirlingBlade3,
                SpellId.WhirlingBlade4, SpellId.WhirlingBlade5, SpellId.WhirlingBlade6,
                SpellId.WhirlingBlade7, SpellId.WhirlingBlade8,
            },
            [DamageType.Pierce] = new[]
            {
                SpellId.ForceBolt1, SpellId.ForceBolt2, SpellId.ForceBolt3,
                SpellId.ForceBolt4, SpellId.ForceBolt5, SpellId.ForceBolt6,
                SpellId.ForceBolt7, SpellId.ForceBolt8,
            },
            [DamageType.Bludgeon] = new[]
            {
                SpellId.ShockWave1, SpellId.ShockWave2, SpellId.ShockWave3,
                SpellId.ShockWave4, SpellId.ShockWave5, SpellId.ShockWave6,
                SpellId.ShockWave7, SpellId.ShockWave8,
            },
            [DamageType.Cold] = new[]
            {
                SpellId.FrostBolt1, SpellId.FrostBolt2, SpellId.FrostBolt3,
                SpellId.FrostBolt4, SpellId.FrostBolt5, SpellId.FrostBolt6,
                SpellId.FrostBolt7, SpellId.FrostBolt8,
            },
            [DamageType.Fire] = new[]
            {
                SpellId.FlameBolt1, SpellId.FlameBolt2, SpellId.FlameBolt3,
                SpellId.FlameBolt4, SpellId.FlameBolt5, SpellId.FlameBolt6,
                SpellId.FlameBolt7, SpellId.FlameBolt8,
            },
            [DamageType.Acid] = new[]
            {
                SpellId.AcidStream1, SpellId.AcidStream2, SpellId.AcidStream3,
                SpellId.AcidStream4, SpellId.AcidStream5, SpellId.AcidStream6,
                SpellId.AcidStream7, SpellId.AcidStream8,
            },
            [DamageType.Electric] = new[]
            {
                SpellId.LightningBolt1, SpellId.LightningBolt2, SpellId.LightningBolt3,
                SpellId.LightningBolt4, SpellId.LightningBolt5, SpellId.LightningBolt6,
                SpellId.LightningBolt7, SpellId.LightningBolt8,
            },
            // Nether Bolt I-VIII, 5349-5356 (contiguous; the Nether STREAKS are the non-contiguous block).
            [DamageType.Nether] = new[]
            {
                SpellId.NetherBolt1, SpellId.NetherBolt2, SpellId.NetherBolt3,
                SpellId.NetherBolt4, SpellId.NetherBolt5, SpellId.NetherBolt6,
                SpellId.NetherBolt7, SpellId.NetherBolt8,
            },
        };

        /// <summary>
        /// Wall - what a Runeblade proc casts INSTEAD of its Blast at a full power bar, once the Blast it would
        /// have thrown is at least the Wall's level (<see cref="WallReplacesBlast"/>). A single rung per type:
        /// ten projectiles, 35/35, spread 0. NETHER HAS NO WALL (no void wall exists in ace_world.spell), so
        /// it is deliberately absent and a Nether Runeblade keeps its Blast. Ids verified by e_Type against
        /// ace_world.spell on 2026-11-01:
        ///
        ///   Acid     Blistering Creeper         1839 -> AcidWall
        ///   Slash    Bed of Blades              1840 -> BladeWall
        ///   Fire     Slithering Flames          1841 -> FlameWall
        ///   Pierce   Spike Strafe               1842 -> ForceWall
        ///   Cold     Foon-Ki's Glacial Floe     1843 -> FrostWall
        ///   Electric Os' Wall                   1844 -> LightningWall
        ///   Bludgeon Hammering Crawler          1845 -> ShockwaveWall
        /// </summary>
        public static readonly IReadOnlyDictionary<DamageType, SpellId> WallByType = new Dictionary<DamageType, SpellId>
        {
            [DamageType.Acid]     = SpellId.AcidWall,
            [DamageType.Slash]    = SpellId.BladeWall,
            [DamageType.Fire]     = SpellId.FlameWall,
            [DamageType.Pierce]   = SpellId.ForceWall,
            [DamageType.Cold]     = SpellId.FrostWall,
            [DamageType.Electric] = SpellId.LightningWall,
            [DamageType.Bludgeon] = SpellId.ShockwaveWall,
        };

        /// <summary>
        /// Ring II - what a Spellstorm proc casts INSTEAD of its Ring at a full power bar. Nine projectiles at
        /// spread 360 like the tier-I ring, about 1.17x its damage (49/49 against 42/42); Clouded Soul II is
        /// 109/63 against Clouded Soul's 94/53. Like the tier-I ids these are NOT in damage-type order.
        /// Verified against ace_world.spell on 2026-11-01:
        ///
        ///   Acid     Searing Disc II            6189 -> SearingDiscII
        ///   Slash    Horizon's Blades II        6190 -> HorizonsBladesII
        ///   Fire     Cassius' Ring of Fire II   6191 -> CassiusRingOfFireII
        ///   Pierce   Nuhmudira's Spines II      6192 -> NuhmudirasSpinesII
        ///   Cold     Halo of Frost II           6193 -> HaloOfFrostII
        ///   Electric Eye of the Storm II        6194 -> EyeOfTheStormII
        ///   Nether   Clouded Soul II            6195 -> CloudedSoulII
        ///   Bludgeon Tectonic Rifts II          6196 -> TectonicRiftsII
        /// </summary>
        public static readonly IReadOnlyDictionary<DamageType, SpellId> RingIIByType = new Dictionary<DamageType, SpellId>
        {
            [DamageType.Acid]     = SpellId.SearingDiscII,
            [DamageType.Slash]    = SpellId.HorizonsBladesII,
            [DamageType.Fire]     = SpellId.CassiusRingOfFireII,
            [DamageType.Pierce]   = SpellId.NuhmudirasSpinesII,
            [DamageType.Cold]     = SpellId.HaloOfFrostII,
            [DamageType.Electric] = SpellId.EyeOfTheStormII,
            [DamageType.Bludgeon] = SpellId.TectonicRiftsII,
            [DamageType.Nether]   = SpellId.CloudedSoulII,
        };

        /// <summary>
        /// The swing's power bar must be at least this to count as "full". The server clamps the client's
        /// power value to [0, 1] on arrival (Player_Melee.HandleActionTargetedMeleeAttack), so a maxed bar is
        /// at most 1.0f. Whether the client sends EXACTLY 1.0f for a bar held at maximum is not verifiable
        /// from the server source (hypothesis: it can send a hair under 1 from a float bar), so the test sits
        /// a hair below 1.0 rather than at equality. The slack is 1%, i.e. at most 0.01 of PowerLevel. It must
        /// stay near 1: any threshold meaningfully below full makes partial power the optimal swing.
        /// </summary>
        public const float FullPowerThreshold = 0.99f;

        /// <summary>
        /// The melee power bar (PowerLevel, 0-1) a hit carried, recovered as PowerMod - 0.5 the way
        /// HeavyBlowsAbility.PowerRatio does (retail melee PowerMod = PowerLevel + 0.5). Returns 0 when the hit
        /// was not a real power-bar melee swing (<paramref name="isPowerBarMeleeSwing"/> false) - a Riposte
        /// counter or any missile hit carries a PowerMod that does not reflect a swing - so no upgrade or
        /// multiplier can fire off it. Clamped to [0, 1].
        /// </summary>
        public static float SwingPowerLevel(float powerMod, bool isPowerBarMeleeSwing)
        {
            if (!isPowerBarMeleeSwing)
                return 0.0f;

            return Math.Clamp(powerMod - 0.5f, 0.0f, 1.0f);
        }

        /// <summary>
        /// <see cref="SwingPowerLevel(float, bool)"/> for a live hit. A hit counts as a power-bar melee swing
        /// when it is Melee AND DamageTarget marked it a real swing (<c>DamageEvent.PowerBarSwing</c>, false for
        /// a Riposte counter).
        /// </summary>
        public static float SwingPowerLevel(DamageEvent damageEvent)
        {
            if (damageEvent == null)
                return 0.0f;

            return SwingPowerLevel(damageEvent.PowerMod, damageEvent.CombatType == CombatType.Melee && damageEvent.PowerBarSwing);
        }

        /// <summary>TRUE when <paramref name="powerLevel"/> is a full bar (see <see cref="FullPowerThreshold"/>).</summary>
        public static bool IsFullPower(float powerLevel)
        {
            return powerLevel >= FullPowerThreshold;
        }

        /// <summary>
        /// Whether a proc casts the upgraded shape: the kill switch is on AND the swing was at a full bar.
        /// </summary>
        public static bool UseFullPowerShape(bool shapeEnabled, float swingPowerLevel)
        {
            return shapeEnabled && IsFullPower(swingPowerLevel);
        }

        /// <summary>
        /// The Runeblade gate: the Wall replaces the Blast only when the Blast rung that would otherwise fire
        /// is at least the Wall's spell level, so the swap never down-levels a high-skill caster's cast.
        /// Pure; the caller supplies <c>Spell.Level</c> for both (read from the loaded spells at runtime).
        /// </summary>
        public static bool WallReplacesBlast(uint blastLevel, uint wallLevel)
        {
            return blastLevel >= wallLevel;
        }

        /// <summary>
        /// The damage multiplier a proc gets from the swing's power bar: 1 + a x PowerLevel, with PowerLevel
        /// clamped to [0, 1] and a negative <paramref name="scalePerPower"/> read as 0. An empty bar returns
        /// exactly 1.0 (so the empty-bar unarmed build is unchanged); a full bar returns 1 + a. Composes
        /// multiplicatively with the Nether parity scale at the call sites. NaN reads as no bonus.
        /// </summary>
        public static float PowerProcScale(float swingPowerLevel, double scalePerPower)
        {
            var a = !double.IsFinite(scalePerPower) || scalePerPower < 0.0 ? 0.0 : scalePerPower;
            var p = float.IsNaN(swingPowerLevel) ? 0.0 : Math.Clamp((double)swingPowerLevel, 0.0, 1.0);

            return (float)(1.0 + a * p);
        }

        // ------------------------------------------------------------------------------------------------
        // Selection
        // ------------------------------------------------------------------------------------------------

        /// <summary>
        /// TRUE when the weapon that actually landed THIS hit is a light weapon.
        ///
        /// READ THE WEAPON, NOT THE CURRENT ATTACK SKILL. The obvious implementation - and the one this
        /// class shipped with first - was "attacker.GetCurrentWeaponSkill() != Skill.LightWeapons", copying
        /// the current-weapon-skill idiom other abilities use to gate on a weapon type. That is WRONG for a
        /// dual-wielding Spellsword, and
        /// silently so: Player.GetCurrentWeaponSkill returns Skill.DualWield instead of the weapon's own
        /// skill whenever the swing is an OFFHAND attack and Dual Wield is the lower skill
        /// (Player_Combat.cs, the IsDualWieldAttack + !DualWieldAlternate branch). Every offhand swing from
        /// such a character would fail the gate and proc nothing, with no message and no log line.
        ///
        /// DamageEvent.Weapon is the correct signal because it is resolved PER HIT:
        /// attacker.GetEquippedMeleeWeapon() returns the OFFHAND weapon on an offhand attack
        /// (Creature_Equipment.cs forwards to GetDualWieldWeapon unless the swing is main-hand), so this
        /// reads whichever blade actually connected. A fire main-hand and a lightning off-hand therefore
        /// proc fire and lightning respectively, which is also the cheapest way to SEE per-hand behaviour
        /// in game.
        /// </summary>
        public static bool IsLightWeaponHit(Player attacker, WorldObject weapon)
        {
            if (attacker == null || weapon == null)
                return false;

            return attacker.ConvertToMoASkill(weapon.WeaponSkill) == Skill.LightWeapons;
        }

        /// <summary>        /// The highest spell LEVEL a given rank of a war proc may throw.
        ///
        /// The design's cap, literally: 3 / 5 / 7 (SPELLSWORD-DESIGN.md sections 1, 3).
        ///
        /// The level-8 Incantation rung is therefore DELIBERATELY OUT OF REACH of the proc. It stays in the
        /// ladders because the ladders describe the spell family truthfully, not because anything can throw
        /// it today; raising this cap to 8, or scaling the thresholds, is the only way to reach it. An
        /// earlier revision capped rank 3 at 8 on the false premise that war magic had no level 7 - it does,
        /// under uniquely named spells rather than roman numerals. See <see cref="StreakLevels"/>.
        /// </summary>
        public static uint MaxLevelForRank(int rank)
        {
            if (rank <= 1)
                return 3;
            if (rank == 2)
                return 5;
            return 7;
        }

        /// <summary>
        /// The engine's own Power floor for a spell level, mirrored from ACE.Server.Entity.SpellFormula's
        /// MinPower table (SpellFormula.cs:95-105), verified against source at write time:
        ///
        ///   level    1   2    3    4    5    6    7    8
        ///   MinPower 1  50  100  150  200  250  300  400
        ///
        /// Mirrored rather than referenced so this table stays a pure, dependency-free function for tests,
        /// and so an out-of-range level returns a defined value instead of throwing out of a combat hook.
        /// The design's rule (section 1d) is "a proc fires the highest level whose MinPower your relevant
        /// magic skill's Current meets" - so the number returned here is compared directly against a
        /// CreatureSkill.Current, which is the whole reason nothing new had to be tuned.
        /// </summary>
        public static uint MinPowerForLevel(uint level)
        {
            switch (level)
            {
                case 1: return 1;
                case 2: return 50;
                case 3: return 100;
                case 4: return 150;
                case 5: return 200;
                case 6: return 250;
                case 7: return 300;
                case 8: return 400;
            }

            // Above the retail ceiling: unreachable by any ladder in this file, but a defined answer beats
            // an exception thrown from inside a melee hit.
            return level > 8 ? 400u : 1u;
        }

        /// <summary>
        /// Picks which rung of a ladder a character can throw: the HIGHEST rung whose level is within
        /// <paramref name="maxLevel"/> and whose <see cref="MinPowerForLevel"/> threshold (scaled by
        /// <paramref name="thresholdScale"/>) is met by <paramref name="magicSkillCurrent"/>.
        ///
        /// Returns an INDEX into the parallel arrays rather than a level, because the ladders are not
        /// contiguous - index 6 of a Streak array is level 8, not level 7.
        ///
        /// When no rung qualifies (a character whose magic skill is below even the lowest rung's threshold),
        /// this returns 0, the lowest rung. That is deliberate: a Spellsword who has bought the ability
        /// always procs something. The alternative - a silent no-op below some skill floor - would make the
        /// entry look broken to exactly the players least able to diagnose it, and the level-1 rungs are
        /// worth ~23 damage, so the floor costs nothing in balance terms.
        /// </summary>
        public static int SelectRungIndex(uint[] rungLevels, uint maxLevel, uint magicSkillCurrent, double thresholdScale)
        {
            if (rungLevels == null || rungLevels.Length == 0)
                return 0;

            for (var i = rungLevels.Length - 1; i >= 0; i--)
            {
                var level = rungLevels[i];

                if (level > maxLevel)
                    continue;

                if (MinPowerForLevel(level) * thresholdScale <= magicSkillCurrent)
                    return i;
            }

            return 0;
        }

        /// <summary>
        /// The spell a proc should cast: the damage type's ladder entry at the rung
        /// <see cref="SelectRungIndex"/> picks, capped by <see cref="MaxLevelForRank"/>.
        ///
        /// Returns NULL when <paramref name="damageType"/> is not one of the eight types the ladders cover
        /// (the seven physical/elemental types plus Nether, added 2026-10-27). DamageEvent.DamageType can
        /// also be Health, Stamina, Mana or Base for exotic sources; those must proc nothing, silently and
        /// with no message (design section 1b's guard). Callers treat null as "no proc this swing".
        ///
        /// Pure and static so the ladder rule can be unit-tested without a Player, a Creature, or a live
        /// spell table.
        /// </summary>
        public static SpellId? SelectSpell(IReadOnlyDictionary<DamageType, SpellId[]> ladder, uint[] rungLevels,
            DamageType damageType, uint magicSkillCurrent, int rank, double thresholdScale)
        {
            if (ladder == null || rungLevels == null)
                return null;

            if (!ladder.TryGetValue(damageType, out var rungs) || rungs == null || rungs.Length == 0)
                return null;

            var index = SelectRungIndex(rungLevels, MaxLevelForRank(rank), magicSkillCurrent, thresholdScale);

            // Defensive: the parallel arrays are the same length by construction, so this only bites if a
            // future edit lengthens one without the other.
            if (index >= rungs.Length)
                index = rungs.Length - 1;

            return rungs[index];
        }

        // ------------------------------------------------------------------------------------------------
        // Nether (Void Magic) procs - owner ruling 2026-10-27
        // ------------------------------------------------------------------------------------------------

        /// <summary>
        /// The ladder every Nether proc is brought to parity with: the Fire ladder at the SAME RUNG INDEX.
        /// All seven elemental ladders are stat-identical rung for rung, so Fire stands for the lot.
        /// </summary>
        public const DamageType ParityReferenceType = DamageType.Fire;

        /// <summary>
        /// The magic skill whose Current picks a war proc's spell level: Item Enchantment for every damage
        /// type. A class-ability proc's level, its target resist roll and its skill damage bonus all read Item
        /// Enchantment (SpellProjectile.CasterSkillOverride), so the three cannot disagree. Nether still casts
        /// a Void spell for its damage type; only the skill that scales it moved. The method keeps its
        /// damage-type parameter so the call sites and a future per-type split stay intact.
        /// </summary>
        public static Skill LevelSkillFor(DamageType damageType)
        {
            return Skill.ItemEnchantment;
        }

        /// <summary>
        /// The average of a uniform roll between <paramref name="minDamage"/> and <paramref name="maxDamage"/>
        /// inclusive, which is how SpellProjectile rolls a war/void spell (ThreadSafeRandom.Next(min, max) on
        /// Spell.MinDamage = base_Intensity and Spell.MaxDamage = base_Intensity + variance).
        /// </summary>
        public static double AverageDamage(int minDamage, int maxDamage)
        {
            return (minDamage + maxDamage) / 2.0;
        }

        /// <summary>
        /// The per-cast damage scale stamped on a Nether proc projectile (SpellProjectile.ProcDamageScale):
        /// the reference (Fire) rung's average over the Nether rung's average, times the
        /// class_ability_spellsword_nether_parity tunable. Retail void spells hit 1.19-2.04x the elemental
        /// ladder per level, which on top of a Nether weapon's cleave would make the void Spellsword far
        /// stronger than its elemental peers; this brings the AVERAGE roll back to the elemental one.
        ///
        /// Returns 1.0 (no rescale) when either average is not positive - missing spell data must not zero
        /// the proc - and treats a negative tunable as 0. Pure for testability.
        /// </summary>
        public static float NetherParityScale(double referenceAverage, double netherAverage, double parity)
        {
            if (referenceAverage <= 0.0 || netherAverage <= 0.0)
                return 1.0f;

            return (float)(referenceAverage / netherAverage * Math.Max(0.0, parity));
        }

        /// <summary>
        /// The parity reference for a laddered proc (Streak or Blast): the Fire spell at the same rung index
        /// as <paramref name="selected"/> in the Nether ladder. NULL for any non-Nether damage type, which is
        /// what keeps every elemental proc unstamped and bit-identical, and NULL if the selected spell is not
        /// in the Nether ladder at all.
        /// </summary>
        public static SpellId? ParityReferenceFor(IReadOnlyDictionary<DamageType, SpellId[]> ladder, DamageType damageType, SpellId selected)
        {
            if (ladder == null || damageType != DamageType.Nether)
                return null;

            if (!ladder.TryGetValue(DamageType.Nether, out var nether) || !ladder.TryGetValue(ParityReferenceType, out var reference))
                return null;

            var index = Array.IndexOf(nether, selected);

            if (index < 0 || index >= reference.Length)
                return null;

            return reference[index];
        }

        /// <summary>
        /// The parity reference for the single-rung Ring (Spellstorm): the Fire ring for a Nether proc, NULL
        /// for every other type.
        /// </summary>
        public static SpellId? RingParityReferenceFor(DamageType damageType)
        {
            return RingParityReferenceFor(damageType, RingByType);
        }

        /// <summary>
        /// <see cref="RingParityReferenceFor(DamageType)"/> over an explicit ring table, so the full-power
        /// Ring II maps Clouded Soul II to Cassius' Ring of Fire II (<see cref="RingIIByType"/>).
        /// </summary>
        public static SpellId? RingParityReferenceFor(DamageType damageType, IReadOnlyDictionary<DamageType, SpellId> ringTable)
        {
            if (damageType != DamageType.Nether || ringTable == null)
                return null;

            return ringTable.TryGetValue(ParityReferenceType, out var reference) ? reference : (SpellId?)null;
        }

        /// <summary>
        /// The live ProcDamageScale for a proc about to cast <paramref name="selected"/>: 1.0 when
        /// <paramref name="parityReference"/> is null (every elemental proc), otherwise
        /// <see cref="NetherParityScale"/> over the two spells' damage as the server loaded it from the
        /// world spell table, so a content edit to either rung moves the scale with it.
        /// </summary>
        public static float ProcDamageScaleFor(SpellId? parityReference, Spell selected, double parity)
        {
            if (parityReference == null || selected == null)
                return 1.0f;

            var reference = new Spell(parityReference.Value);

            return NetherParityScale(AverageDamage(reference.MinDamage, reference.MaxDamage),
                AverageDamage(selected.MinDamage, selected.MaxDamage),
                parity);
        }

        // ------------------------------------------------------------------------------------------------
        // Sundermark's Nether curses
        // ------------------------------------------------------------------------------------------------

        /// <summary>
        /// The three void damage-over-time families Sundermark applies on a Nether hit, ONE picked uniformly
        /// at random per proc. Each array is parallel to <see cref="VulnerabilityLevels"/> (levels 1-8), so
        /// the same SelectRungIndex result (on Life Magic, as for the vulnerabilities) indexes all three.
        /// Resolved from the SpellId enum:
        ///
        ///   Corruption         5395-5402
        ///   Corrosion          5387-5394
        ///   Destructive Curse  5339-5344 for I-VI, then 5337 (VII) and 5338 (the Incantation) - the
        ///                      level-7 and level-8 rows sit BELOW level I in the id space.
        ///
        /// They fall in three separate spell categories, so the three never overwrite one another.
        /// </summary>
        public static readonly SpellId[][] NetherCurseFamilies =
        {
            new[]
            {
                SpellId.Corruption1, SpellId.Corruption2, SpellId.Corruption3, SpellId.Corruption4,
                SpellId.Corruption5, SpellId.Corruption6, SpellId.Corruption7, SpellId.Corruption8,
            },
            new[]
            {
                SpellId.Corrosion1, SpellId.Corrosion2, SpellId.Corrosion3, SpellId.Corrosion4,
                SpellId.Corrosion5, SpellId.Corrosion6, SpellId.Corrosion7, SpellId.Corrosion8,
            },
            new[]
            {
                SpellId.CurseDestructionOther1, SpellId.CurseDestructionOther2, SpellId.CurseDestructionOther3,
                SpellId.CurseDestructionOther4, SpellId.CurseDestructionOther5, SpellId.CurseDestructionOther6,
                SpellId.CurseDestructionOther7, SpellId.CurseDestructionOther8,
            },
        };

        /// <summary>
        /// The curse for a family roll and a rung index. <paramref name="familyRoll"/> is the uniform pick in
        /// [0, NetherCurseFamilies.Length - 1]; out-of-range values clamp rather than throw out of a combat hook.
        /// </summary>
        public static SpellId NetherCurseFor(int familyRoll, int rungIndex)
        {
            var family = NetherCurseFamilies[Math.Clamp(familyRoll, 0, NetherCurseFamilies.Length - 1)];

            return family[Math.Clamp(rungIndex, 0, family.Length - 1)];
        }
    }
}
