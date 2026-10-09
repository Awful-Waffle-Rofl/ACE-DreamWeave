using System;
using System.Collections.Generic;

using ACE.Entity.Enum;
using ACE.Server.Managers;

namespace ACE.Server.MlDigsite
{
    /// <summary>
    /// THE single place this system reads PropertyManager. Every other file in ACE.Server.MlDigsite takes
    /// its numbers as parameters, which is what keeps MlDigsiteRules pure and unit-testable (PropertyManager
    /// reads throw under the test harness) and what stops the same key being read with two different
    /// fallbacks in two places.
    ///
    /// EVERY READ PASSES AN EXPLICIT FALLBACK, and that is not decoration. PropertyManager.GetBool/GetLong/
    /// GetDouble fall back to the TYPE default (false / 0 / 0.0) for a key that has not yet been seeded from
    /// DefaultBooleanProperties, so a switch shipped default-true would read FALSE until the first sync -
    /// the same trap ThreadDungeonRewardSpawner documents at its dynamic_dungeons_boss_chest_enabled read.
    /// The fallback here is always the registered default.
    ///
    /// Per this repo's standing rule, the master switch ships ON: it is a kill switch for an operator who
    /// needs the feature off, never a gate that has to be found and turned on before the feature works.
    /// </summary>
    public static class MlDigsiteTunables
    {
        // ---- master switch ------------------------------------------------------------------------------

        public static bool Enabled => PropertyManager.GetBool("ml_digsite_enabled", true).Item;

        // ---- the weighted type roll ---------------------------------------------------------------------

        // Round 16 owner ruling: 40 / 30 / 30, up from the round-13 70 / 25 / 5 - see
        // Database/Updates/Shard/2026-09-21-01-MlDigsiteRozR16.sql for the guarded migration off the old defaults.
        public static long WeightWaves => PropertyManager.GetLong("ml_digsite_weight_waves", 40).Item;
        public static long WeightCorruption => PropertyManager.GetLong("ml_digsite_weight_corruption", 30).Item;
        public static long WeightBossRush => PropertyManager.GetLong("ml_digsite_weight_bossrush", 30).Item;

        // ---- admission ----------------------------------------------------------------------------------

        public static long MaxConcurrent => PropertyManager.GetLong("ml_digsite_max_concurrent", 20).Item;

        /// <summary>Metres between two live encounter anchors below which a second one is refused.</summary>
        public static double SeparationMetres => PropertyManager.GetDouble("ml_digsite_separation_metres", 80.0).Item;

        // ---- lifetime -----------------------------------------------------------------------------------

        /// <summary>
        /// Round 16 owner ruling: a hard 10-minute cap on EVERY encounter shape, down from 1800s (30 min).
        /// The run ends here regardless of progress - see Database/Updates/Shard/2026-09-21-01-MlDigsiteRozR16.sql.
        /// </summary>
        public static TimeSpan Ttl => Seconds(PropertyManager.GetLong("ml_digsite_ttl_seconds", 600).Item);

        /// <summary>
        /// Seconds with nobody ALIVE inside the audience radius before the reap ends the encounter as Wiped
        /// (MlDigsiteRules.ShouldEnd). Short on purpose: a wipe pays the tier reached, so this only has to cover
        /// a corpse run back, and 0 disables the test.
        /// </summary>
        public static TimeSpan WipeGrace => Seconds(PropertyManager.GetLong("ml_digsite_wipe_grace_seconds", 30).Item);

        // ---- audience -----------------------------------------------------------------------------------

        /// <summary>
        /// Radius of the one scan that decides difficulty, announcements, presence and payout
        /// (MlDigsiteAudience). One number rather than three so a player who is close enough to make the
        /// fight harder is by construction close enough to be paid for it.
        /// </summary>
        public static float AudienceRadiusMetres
            => (float)Math.Clamp(PropertyManager.GetDouble("ml_digsite_audience_radius_metres", 60.0).Item, 1.0, 1000.0);

        /// <summary>How far from the anchor encounter creatures are scattered.</summary>
        public static float SpawnRadiusMetres
            => (float)Math.Clamp(PropertyManager.GetDouble("ml_digsite_spawn_radius_metres", 12.0).Item, 1.0, 100.0);

        // ---- waves --------------------------------------------------------------------------------------

        /// <summary>Round 16 owner ruling: 1s, down from 10s - the next wave lands almost immediately after the field clears.</summary>
        public static TimeSpan InterWaveDelay => Seconds(PropertyManager.GetLong("ml_digsite_inter_wave_seconds", 1).Item);

        /// <summary>0 disables the clock, matching the shipped wave portal's own semantics (weenie 1001550, PropertyFloat 9006).</summary>
        public static TimeSpan WaveTimeLimit => Seconds(PropertyManager.GetLong("ml_digsite_wave_time_limit_seconds", 300).Item);

        /// <summary>0 disables the clock, matching the shipped wave portal's own semantics (weenie 1001550, PropertyFloat 9005).</summary>
        public static TimeSpan StallTimeout => Seconds(PropertyManager.GetLong("ml_digsite_stall_timeout_seconds", 150).Item);

        // ---- endless waves ------------------------------------------------------------------------------

        /// <summary>A checkpoint mini-boss spawns alongside every Nth wave. 0 or less turns checkpoints off.</summary>
        public static long CheckpointEvery => PropertyManager.GetLong("ml_digsite_checkpoint_every", 3).Item;

        /// <summary>Tier fraction added per checkpoint mini-boss killed.</summary>
        public static double CheckpointBonus => PropertyManager.GetDouble("ml_digsite_checkpoint_bonus", 0.05).Item;

        /// <summary>
        /// Elapsed encounter time after which an endless Waves run is owed ONE forced checkpoint mini-boss,
        /// whatever wave it happens to be on (MlDigsiteRules.ForcedCheckpointDue). 0 turns the forced spawn
        /// off and leaves only the wave-count cadence of <see cref="CheckpointEvery"/>.
        /// </summary>
        public static TimeSpan ForcedCheckpointAfter
            => Seconds(PropertyManager.GetLong("ml_digsite_forced_miniboss_seconds", 180).Item);

        /// <summary>Extra creatures per wave after the first, on top of the crowd curve.</summary>
        public static long WaveCountPerWave => PropertyManager.GetLong("ml_digsite_wave_count_per_wave", 1).Item;

        /// <summary>Health multiplier added per wave after the first (MlDigsiteRules.WaveHealthMultiplier).</summary>
        public static double WaveHealthPerWave => PropertyManager.GetDouble("ml_digsite_wave_health_per_wave", 0.10).Item;

        /// <summary>Ceiling on the per-wave health multiplier.</summary>
        public static double WaveHealthCap => PropertyManager.GetDouble("ml_digsite_wave_health_cap", 3.0).Item;

        /// <summary>DamageRating added per wave after the first (MlDigsiteRules.WaveDamageRatingAddend).</summary>
        public static long WaveDrPerWave => PropertyManager.GetLong("ml_digsite_wave_dr_per_wave", 5).Item;

        /// <summary>Ceiling on the per-wave DamageRating addend.</summary>
        public static long WaveDrCap => PropertyManager.GetLong("ml_digsite_wave_dr_cap", 60).Item;

        /// <summary>
        /// The endless-waves payout table (MlDigsiteRules.ParseTierTable). Parsed once per distinct value
        /// rather than on every read - the status line reads it every tick. A value that parses to nothing
        /// falls back to the shipped default, so a mis-typed table can never zero the payout.
        /// </summary>
        public static IReadOnlyList<MlDigsiteWaveTier> WaveTiers
        {
            get
            {
                var raw = PropertyManager.GetString("ml_digsite_wave_tiers", MlDigsiteRules.DefaultWaveTiers).Item;

                var cached = waveTiersCache;

                if (cached != null && cached.Item1 == raw)
                    return cached.Item2;

                var parsed = MlDigsiteRules.ParseTierTable(raw);

                if (parsed.Count == 0)
                    parsed = MlDigsiteRules.ParseTierTable(MlDigsiteRules.DefaultWaveTiers);

                waveTiersCache = Tuple.Create(raw, parsed);

                return parsed;
            }
        }

        private static Tuple<string, IReadOnlyList<MlDigsiteWaveTier>> waveTiersCache;

        public static int WaveCountBase => (int)Math.Clamp(PropertyManager.GetLong("ml_digsite_wave_count_base", 3).Item, 0, 200);
        public static int WaveCountCap => (int)Math.Clamp(PropertyManager.GetLong("ml_digsite_wave_count_cap", 12).Item, 0, 200);
        public static double WaveCountPerParticipant => PropertyManager.GetDouble("ml_digsite_wave_count_per_participant", 1.5).Item;

        // ---- corruption: Corrupted mob kill count (round 16 redesign) -----------------------------------
        //
        // The meter mechanic (a climbing fill that failed the run at 100%) is RETIRED per round 16 owner
        // ruling: a priority mob no longer merely survives to fill a meter. Instead one "Corrupted" mob is
        // alive at a time - tougher than the surrounding field - and killing ml_digsite_corruption_kills_required
        // of them (one at a time; the next spawns only once the last is dead) wins the encounter outright.

        /// <summary>Corrupted mobs the players must kill to win (MlDigsiteRules.CorruptionKillsWon).</summary>
        public static int CorruptionKillsRequired
            => (int)Math.Clamp(PropertyManager.GetLong("ml_digsite_corruption_kills_required", MlDigsiteRules.DefaultCorruptionKillsRequired).Item, 1, 100);

        /// <summary>
        /// How much more health a Corrupted mob has than an ordinary field mob of the same wcid/scaling,
        /// applied via MlDigsiteBossMechanicRules.ScaledAddHealth ON TOP of the ordinary crowd/wave scaling
        /// every digsite creature already gets.
        /// </summary>
        public static double CorruptedHealthMultiplier
            => Math.Max(0.0, PropertyManager.GetDouble("ml_digsite_corrupted_health_multiplier", 6.0).Item);

        /// <summary>
        /// Round 17: the Corrupted mob's power gain. While a Corruption encounter's Corrupted mob is alive,
        /// every <see cref="CorruptionPowerTickInterval"/> the rest of the field gains this much more damage,
        /// as a fraction (0.03 = +3%, applied as +3 DamageRating through MlDigsiteRules.DamageRatingFor, which
        /// caps the total at MlDigsiteRules.MaxDamageRatingGrowth). Resets when that Corrupted mob dies.
        /// 0 turns the gain off.
        /// </summary>
        public static double CorruptionPowerPerTick
            => Math.Clamp(PropertyManager.GetDouble("ml_digsite_corruption_power_per_tick", 0.03).Item, 0.0, 10.0);

        /// <summary>
        /// Seconds between two power gains (the pre-round-16 meter's own 5 s cadence, which is where the
        /// +3%-per-tick formula came from). 0 turns the gain off. Quantised to the digsite's 1 s tick.
        /// </summary>
        public static TimeSpan CorruptionPowerTickInterval
            => Seconds(PropertyManager.GetLong("ml_digsite_corruption_power_tick_seconds", 5).Item);

        /// <summary>How often more field mobs spill into a live Corruption encounter.</summary>
        public static TimeSpan CorruptionFieldSpawnInterval
            => Seconds(PropertyManager.GetLong("ml_digsite_corruption_field_spawn_seconds", 20).Item);

        /// <summary>
        /// How many more field mobs spawn per interval, capped by however much room is left under
        /// MlDigsiteRules.MaxWaveSpawnCount live bodies.
        /// </summary>
        public static int CorruptionFieldSpawnCount
            => (int)Math.Clamp(PropertyManager.GetLong("ml_digsite_corruption_field_spawn_count", 5).Item, 0, MlDigsiteRules.MaxWaveSpawnCount);

        // ---- rewards ------------------------------------------------------------------------------------

        public static long ChestLootCount => Math.Clamp(PropertyManager.GetLong("ml_digsite_chest_loot_count", 15).Item, 0, 100);
        /// <summary>
        /// Round 17 owner ruling: 2 per map, down from 5 - the dig step itself now pays none
        /// (ml_treasure_payout_min/max 0). Scaled by MlDigsiteRules.ScaleCurrency, which floors any positive
        /// fraction at 1, so a partial clear still pays one.
        /// </summary>
        public static long ChestDoubloons => Math.Clamp(PropertyManager.GetLong("ml_digsite_chest_doubloons", 2).Item, 0, 1000);
        public static long ChestTradeNotes => Math.Clamp(PropertyManager.GetLong("ml_digsite_chest_trade_notes", 5).Item, 0, 1000);

        /// <summary>
        /// The treasure_death profile the chest rolls its loot from, as a treasure_death.treasure_Type - the
        /// column WorldDatabaseWithEntityCache.GetCachedDeathTreasure looks up, and the same one a weenie's
        /// DeathTreasureType joins - NEVER the row's id. 2001 is the retail Legendary Chest's own profile
        /// (row id 241, tier 8), which is the same table the Thread boss cache imitates
        /// (ThreadDungeonRewardSpawner.CacheMagicItemProfile) - Weapon/Armor/Clothing/Jewelry/Cloak/PetDevice
        /// only, never the Scroll/Gem/ArtObject types an ordinary creature corpse can roll.
        ///
        /// Round 15: this shipped as 241, the row id. No treasure_death row has treasure_Type 241, so every
        /// digsite chest resolved no profile and carried currency only.
        /// </summary>
        public static uint ChestTreasureDeathId
            => (uint)Math.Clamp(PropertyManager.GetLong("ml_digsite_chest_treasure_death_id", DefaultChestTreasureType).Item, 0, uint.MaxValue);

        /// <summary>The shipped ml_digsite_chest_treasure_death_id: the Legendary Chest's treasure_Type (row id 241).</summary>
        public const long DefaultChestTreasureType = 2001;

        /// <summary>
        /// A FINITE chest lifetime, and this is the one number that must never become -1. The Thread boss
        /// cache uses TimeToRot = -1 because it stands inside an ephemeral copy that is destroyed wholesale
        /// when the run ends; a digsite chest stands on a live, persistent, shared outdoor landblock, where
        /// -1 would leave it on the island until the landblock unloaded.
        /// </summary>
        public static double ChestTtlSeconds
            => Math.Clamp(PropertyManager.GetLong("ml_digsite_chest_ttl_seconds", 600).Item, 30, 86400);

        /// <summary>
        /// XP and luminance for one COMPLETION (round 15 owner ruling: 425,000,000 XP and 80,000 luminance,
        /// flat). A FullClear pays exactly these, never scaled by the payout fraction
        /// (MlDigsiteRules.XpLuminanceFraction); every other outcome pays its fraction of them.
        /// </summary>
        public static long FullXp => Math.Max(0, PropertyManager.GetLong("ml_digsite_full_xp", DefaultFullXp).Item);
        public static long FullLuminance => Math.Max(0, PropertyManager.GetLong("ml_digsite_full_luminance", DefaultFullLuminance).Item);

        public const long DefaultFullXp = 425000000;
        public const long DefaultFullLuminance = 80000;

        /// <summary>
        /// DEAD as of round 16: EncounterPayoutFraction's Corruption branch no longer reads this - Corruption
        /// stopped being binary (see MlDigsiteRules.CorruptionProgressFraction). Left wired (and still fed
        /// into MlDigsitePayoutTunables.PartialFraction below) rather than removed, so the many existing test
        /// call sites that set PartialFraction on a hand-built MlDigsitePayoutTunables do not need to change.
        /// </summary>
        public static double PartialPayoutFraction
            => PropertyManager.GetDouble("ml_digsite_partial_payout_fraction", 0.35).Item;

        /// <summary>
        /// Round 16 owner ruling: every non-WIN outcome pays its progress fraction times this (shipped 0.85),
        /// instead of the progress fraction straight. A WIN (FullClear) is never multiplied by this at all -
        /// see MlDigsiteRules.EncounterPayoutFraction.
        /// </summary>
        public static double FailPayoutMultiplier
            => Math.Clamp(PropertyManager.GetDouble("ml_digsite_fail_payout_multiplier", 0.85).Item, 0.0, 1.0);

        /// <summary>
        /// The monster-effect overlay the Boss Rush boss carries on top of its authored effects
        /// (Creature.ApplyMonsterEffectOverlay). Empty means no overlay.
        /// </summary>
        public static string BossRushMechanic
            => PropertyManager.GetString("ml_digsite_bossrush_mechanic", MlDigsiteRules.DefaultBossRushMechanic).Item;

        /// <summary>The least a Boss Rush that did not kill its boss pays (MlDigsiteRules.BossRushFraction).</summary>
        public static double BossRushMinFraction
            => PropertyManager.GetDouble("ml_digsite_bossrush_min_fraction", 0.10).Item;

        // ---- Boss Rush mechanic sets (RoZ round 13, "Boss mechanics test") -------------------------------
        //
        // Composition and NUMBERS are separate keys on purpose: ml_digsite_bossrush_sets says which mechanics
        // a set runs, and each mechanic's own string says what its numbers are. One key holding thirty numbers
        // is unreadable, and a single typo in it would fall the whole table back to its default.
        //
        // Each of the six strings below is read through MlDigsiteBossMechanicRules, which is pure; this class
        // stays the one PropertyManager read point for the whole system.

        /// <summary>
        /// The mechanic driver's own kill switch. Ships ON (this class's standing rule): an operator who needs
        /// Boss Rush back to its plain single-target fight turns this off and the driver becomes a no-op,
        /// leaving the boss, its tether and the ml_digsite_bossrush_mechanic overlay exactly as they were.
        /// </summary>
        public static bool BossRushMechanicsEnabled
            => PropertyManager.GetBool("ml_digsite_bossrush_mechanics_enabled", true).Item;

        /// <summary>
        /// The set table (MlDigsiteBossMechanicRules.ParseSets). Parsed once per distinct value rather than on
        /// every read - the driver consults it on the roll and the status line shows it. A value that parses to
        /// nothing falls back to the shipped default, so a mis-typed table can never leave Boss Rush with no
        /// mechanics at all. Same caching shape as <see cref="WaveTiers"/>.
        /// </summary>
        public static IReadOnlyList<MlDigsiteMechanicSet> BossRushSets
        {
            get
            {
                var raw = PropertyManager.GetString("ml_digsite_bossrush_sets", MlDigsiteBossMechanicRules.DefaultSets).Item;

                var cached = bossRushSetsCache;

                if (cached != null && cached.Item1 == raw)
                    return cached.Item2;

                var parsed = MlDigsiteBossMechanicRules.ParseSets(raw);

                if (parsed.Count == 0)
                    parsed = MlDigsiteBossMechanicRules.ParseSets(MlDigsiteBossMechanicRules.DefaultSets);

                bossRushSetsCache = Tuple.Create(raw, parsed);

                return parsed;
            }
        }

        private static Tuple<string, IReadOnlyList<MlDigsiteMechanicSet>> bossRushSetsCache;

        /// <summary>
        /// 0 rolls the set; any other value pins the set carrying that authored set= id, for a tester who has
        /// to be able to reach one specific set from a client rather than by seeding an rng. A pinned id the
        /// table does not carry falls back to the roll (MlDigsiteBossMechanicRules.RollSet).
        /// </summary>
        public static long BossRushSetForce => PropertyManager.GetLong("ml_digsite_bossrush_set_force", 0).Item;

        /// <summary>
        /// How much longer a SECONDARY mechanic's cadence is than the same mechanic's would be as a main. Not
        /// a different code path - the same module on a slower clock, so a set's secondary is present without
        /// competing with its main for the player's attention.
        /// </summary>
        public static double BossRushSecondaryCadenceMult
            => Math.Clamp(PropertyManager.GetDouble("ml_digsite_bossrush_secondary_cadence_mult", 2.0).Item, 1.0, 10.0);

        public static MlDigsiteMechanicArgs BossRushVolatileArgs
            => ReadArgs("ml_digsite_bossrush_volatile", MlDigsiteBossMechanicRules.DefaultVolatile, ref volatileArgsCache);

        public static MlDigsiteMechanicArgs BossRushDrumsArgs
            => ReadArgs("ml_digsite_bossrush_drums", MlDigsiteBossMechanicRules.DefaultDrums, ref drumsArgsCache);

        public static MlDigsiteMechanicArgs BossRushImmuneArgs
            => ReadArgs("ml_digsite_bossrush_immune", MlDigsiteBossMechanicRules.DefaultImmune, ref immuneArgsCache);

        public static MlDigsiteMechanicArgs BossRushInterruptArgs
            => ReadArgs("ml_digsite_bossrush_interrupt", MlDigsiteBossMechanicRules.DefaultInterrupt, ref interruptArgsCache);

        public static MlDigsiteMechanicArgs BossRushSafeZonesArgs
            => ReadArgs("ml_digsite_bossrush_safezones", MlDigsiteBossMechanicRules.DefaultSafeZones, ref safeZonesArgsCache);

        private static Tuple<string, MlDigsiteMechanicArgs> volatileArgsCache;
        private static Tuple<string, MlDigsiteMechanicArgs> drumsArgsCache;
        private static Tuple<string, MlDigsiteMechanicArgs> immuneArgsCache;
        private static Tuple<string, MlDigsiteMechanicArgs> interruptArgsCache;
        private static Tuple<string, MlDigsiteMechanicArgs> safeZonesArgsCache;

        /// <summary>
        /// One mechanic's args, parsed once per distinct value. A module reads its args on every driver tick,
        /// so the parse is cached the same way the two tables above are; the cache is a single reference
        /// assignment, which is atomic, and a racing writer costs at most one redundant parse.
        /// </summary>
        private static MlDigsiteMechanicArgs ReadArgs(string key, string shippedDefault, ref Tuple<string, MlDigsiteMechanicArgs> cache)
        {
            var raw = PropertyManager.GetString(key, shippedDefault).Item;

            var cached = cache;

            if (cached != null && cached.Item1 == raw)
                return cached.Item2;

            var parsed = MlDigsiteBossMechanicRules.ParseArgs(raw);

            cache = Tuple.Create(raw, parsed);

            return parsed;
        }

        /// <summary>
        /// The telegraph marker placed where a hit IS about to land (a drum shape, a volatile add's corpse).
        /// Default 1002665 "Pillar of Fire": an already-shipped Generic/UiHidden particle column with
        /// PhysicsState Static|Ethereal|IgnoreCollisions|LightingOn, so it is visible, has no collision and no
        /// cursor - exactly what a ground telegraph wants. 0 disables markers, leaving the chat lines.
        /// </summary>
        public static uint BossRushHazardMarkerWcid
            => (uint)Math.Clamp(PropertyManager.GetLong("ml_digsite_bossrush_hazard_marker_wcid", 1002665).Item, 0, uint.MaxValue);

        /// <summary>
        /// The marker placed where a player is SAFE (a shifting safe zone). Default 1002667 "Pillar of Frost",
        /// the same shipped scenery recipe as the hazard marker in a different colour, so safe and lethal
        /// ground never read as the same thing. 0 disables markers, leaving the chat lines.
        /// </summary>
        public static uint BossRushSafeMarkerWcid
            => (uint)Math.Clamp(PropertyManager.GetLong("ml_digsite_bossrush_safe_marker_wcid", 1002667).Item, 0, uint.MaxValue);

        /// <summary>
        /// The usable object the interrupt mechanic places. Default 1005950 "Aun Signal Drum", authored for
        /// this feature (Content/sql/weenies). 0 disables the placement, which turns the interrupt into an
        /// unavoidable hit - so the module refuses to run at all rather than punishing a player for a mechanic
        /// they were never given.
        /// </summary>
        public static uint BossRushInterruptWcid
            => (uint)Math.Clamp(PropertyManager.GetLong("ml_digsite_bossrush_interrupt_wcid", 1005950).Item, 0, uint.MaxValue);

        /// <summary>
        /// The motion the boss plays on each drum beat (DrumCadenceMechanic), as a raw MotionCommand value.
        /// Default 268435554 = MotionCommand.AttackHigh1, chosen by evidence (RoZ playtest feedback: the drum
        /// beat had a chat line and a sound but no visible boss animation): probed with the ACE.Content.Tools
        /// `motionlength` command against every Boss Rush boss's own MotionTable id (read from its weenie SQL
        /// under Content/sql/weenies) in MotionStance.HandCombat - the stance every one of these six named
        /// apex units and all eight Kept Siraluun substitutes actually fights in (Melee CombatMode, no
        /// equipped weapon in their weenie SQL, so no wielded-weapon stance applies). AttackHigh1 has a
        /// non-zero reach length (MotionTable.GetAnimationLength(stance, motion, null)) in all six distinct
        /// MotionTable ids the roster carries (0x090000BB, 0x0900009C, 0x090000A0, 0x09000002, 0x09000069,
        /// 0x09000214) and reads as a heavy overhead strike, matching the drum's "the boss strikes the drum"
        /// line. 0 disables the broadcast entirely (no animation, matching today's behavior).
        /// </summary>
        public static long BossRushDrumMotion => PropertyManager.GetLong("ml_digsite_bossrush_drum_motion", 268435554).Item;

        /// <summary>
        /// The retail spell id the drum cadence's Ring shape casts at resolve. Default 3993 "Heavy Blade
        /// Ring" - see the tunable's own registered description (PropertyManager.cs) for the evidence. 0, or
        /// any id that does not classify as SpellProjectile.GetProjectileSpellType == Ring, falls back to the
        /// original geometric damage and is never thrown on.
        /// </summary>
        public static long BossRushDrumRingSpell => PropertyManager.GetLong("ml_digsite_bossrush_drum_ring_spell", 3993).Item;

        /// <summary>The Wall counterpart to <see cref="BossRushDrumRingSpell"/>. Default 1844 "Os' Wall".</summary>
        public static long BossRushDrumWallSpell => PropertyManager.GetLong("ml_digsite_bossrush_drum_wall_spell", 1844).Item;

        /// <summary>
        /// The most creatures (boss included) that cast the drum cadence's Ring/Wall spell in one resolve.
        /// Clamped to [1, 50] so a mistuned value can neither disable casting nor spawn an unbounded number
        /// of projectile sets.
        /// </summary>
        public static long BossRushDrumMaxCasters
            => Math.Clamp(PropertyManager.GetLong("ml_digsite_bossrush_drum_max_casters", 8).Item, 1, 50);

        // ---- the leash heal (RoZ round 13, "Open-area safeguard") ----------------------------------------

        /// <summary>
        /// Whether a Boss Rush boss that has been pulled past its tether and is walking home is healed to full
        /// when it gets there. Ships ON (this class's standing rule).
        ///
        /// SCOPED TO THE BOSS RUSH BOSS, not to every digsite creature and emphatically not to every monster in
        /// the world: the tester asked about bosses, and healing on the shared navigation path would make every
        /// monster on the server un-attritionable.
        /// </summary>
        public static bool TetherHealEnabled => PropertyManager.GetBool("ml_digsite_tether_heal_enabled", true).Item;

        // ---- group rewards ----------------------------------------------------------------------------------

        /// <summary>The share pool divided between helpers' chests (MlDigsiteRules.NonOwnerShare).</summary>
        public static double GroupSharePool => PropertyManager.GetDouble("ml_digsite_group_share_pool", 1.5).Item;

        /// <summary>The most one helper's chest pays, as a share of the tier.</summary>
        public static double GroupShareMax => PropertyManager.GetDouble("ml_digsite_group_share_max", 0.75).Item;

        /// <summary>The least one helper's chest pays, as a share of the tier.</summary>
        public static double GroupShareMin => PropertyManager.GetDouble("ml_digsite_group_share_min", 0.20).Item;

        /// <summary>
        /// How recently a helper must have been seen alive at the site (the reap stamps it every 15 s) to be
        /// paid. Deliberately longer than the wipe grace, so a wipe still pays the group that fought.
        /// </summary>
        public static TimeSpan PresenceWindow => Seconds(PropertyManager.GetLong("ml_digsite_presence_window_seconds", 90).Item);

        /// <summary>Every tunable MlDigsiteRules.EncounterPayoutFraction reads, gathered in one read.</summary>
        public static MlDigsitePayoutTunables Payout => new MlDigsitePayoutTunables
        {
            Tiers = WaveTiers,
            CheckpointBonus = CheckpointBonus,
            BossRushMinFraction = BossRushMinFraction,
            PartialFraction = PartialPayoutFraction,
            FailPayoutMultiplier = FailPayoutMultiplier,
        };

        // ---- the Kept Siraluun rare miniboss --------------------------------------------------------------

        /// <summary>
        /// Chance that the first checkpoint mini-boss (Waves shape) or the boss (Boss Rush shape) is replaced
        /// by a rare Kept Siraluun instead - at most one roll per encounter (MlDigsiteEncounter
        /// .TryClaimKeptSiraluunRoll). Clamped into [0, 1] so a mis-set tunable can neither refuse the roll
        /// outright nor force it below what MlDigsiteRules.RollKeptSiraluun already guarantees at 1.0.
        /// </summary>
        public static double KeptSiraluunChance
            => Math.Clamp(PropertyManager.GetDouble("ml_digsite_kept_siraluun_chance", 0.05).Item, 0.0, 1.0);

        // ---- crowd scaling ----------------------------------------------------------------------------------

        /// <summary>
        /// The digsite system's OWN crowd-scaling dials, resolved the same way the Bluespire ladder's D6
        /// crowd scaling is (see BluespireCrowdScaling / CrowdHealthDef.Resolve), but never sharing its keys:
        /// this island's mobs must be retunable independently of that dungeon's.
        /// </summary>
        public static double CrowdPerPlayer => PropertyManager.GetDouble("ml_digsite_crowd_per_player", 0.15).Item;

        public static double CrowdHealthCap => PropertyManager.GetDouble("ml_digsite_crowd_health_cap", 3.0).Item;

        public static long CrowdDrPerPlayer => PropertyManager.GetLong("ml_digsite_crowd_dr_per_player", 10).Item;

        public static long CrowdDrCap => PropertyManager.GetLong("ml_digsite_crowd_dr_cap", 100).Item;

        // ---- feedback: status line (round 13 feedback item E) ---------------------------------------------
        //
        // ml_digsite_broadcast_radius_metres is RETIRED (round 17): the two opening lines briefly went
        // realm-wide (MlDigsiteManager.AnnounceOpening) before OWNER RULING (2026-09-24) retired them outright
        // ("Treasure map events should not have world event announcements or progress updates") - so nothing
        // reads a broadcast radius any more either way. The shard row is deleted by
        // Database/Updates/Shard/2026-09-22-02-MlDigsiteRetireBroadcastRadius.sql.

        /// <summary>How often (seconds) the periodic /digsite-style status line repeats for participants.</summary>
        public static TimeSpan StatusInterval
            => Seconds(PropertyManager.GetLong("ml_digsite_status_interval_seconds", 30).Item);

        // ---- priority mob visual tag (round 13 feedback item F) -------------------------------------------

        /// <summary>
        /// PropertyDataId.PhysicsScript (30) stamped on a Priority-role creature so it reads as visibly
        /// different from ordinary field wildlife (MlDigsiteSpawner.TrySpawn, before EnterWorld) and
        /// re-broadcast on every corruption meter tick (DriveCorruption) so a player who walks out of
        /// visual range and back still sees it tagged.
        ///
        /// Default: PlayScript.BreatheAcid (0x56). Candidates considered, from the only persistent
        /// (non-momentary) PhysicsScript defaults already shipped in this fork's weenie SQL - grepped
        /// as `, 30, 0x0000...` /* PhysicsScript */ across Content/sql/weenies:
        ///   - BreatheAcid (0x56): stamped on "Mirebound Chanter/Reaver", "Mire-Sworn Ravener",
        ///     "Harrowmoss the Green Silence" and "Drift-Touched Olthoi Soldier" - a continuous looping
        ///     acid-drip aura, already this fork's go-to for "corrupted/toxic and dangerous". CHOSEN: the
        ///     corruption meter is themed as a spreading taint, so this reads correctly out of the box.
        ///   - BreatheFlame (0x54): stamped on "Sedgewrack the Drowned" and "Mirebound Chanter" - a
        ///     looping fire-breath aura used as a generic "this one is stronger" tag rather than anything
        ///     fire-specific.
        ///   - RestrictionEffectBlue (0x98, House.cs:77): the only non-weenie-SQL persistent
        ///     DefaultScriptId precedent in C#, a static blue ward shimmer - reads as protective/territorial
        ///     rather than hostile, so not a fit for a priority KILL target.
        /// The owner can retune this live; nothing else in the digsite code assumes a specific value.
        /// </summary>
        public static uint PriorityScriptId
            => (uint)Math.Clamp(PropertyManager.GetLong("ml_digsite_priority_script", 0x56).Item, 0, uint.MaxValue);

        /// <summary>ObjScale multiplier stamped on a Priority-role creature, alongside the script tag.</summary>
        public static float PriorityScaleMultiplier
            => (float)Math.Clamp(PropertyManager.GetDouble("ml_digsite_priority_scale", 1.25).Item, 0.1, 10.0);

        // ---- radar tag (round 17 tester feedback) --------------------------------------------------------

        /// <summary>
        /// PropertyInt.RadarBlipColor stamped on EVERY digsite-spawned creature (MlDigsiteSpawner.TrySpawn,
        /// before EnterWorld), so a digsite spawn reads differently on radar from the island's own ambient
        /// wildlife it otherwise shares a wcid and a name with (round 17 tester feedback: "killed everything
        /// on radar" but the wave never cleared - the survivors were ordinary wildlife the player could not
        /// tell apart from the fight). Default RadarColor.White (3), against RadarColor.Creature's own
        /// default of Gold (0x02) an ordinary unstamped monster carries.
        ///
        /// Null means no stamp at all - the tunable at or below zero, matching the "0 disables" convention
        /// the rest of this class uses, and clamped into a byte's range (RadarColor's own backing type)
        /// otherwise, so an out-of-range value never throws casting into the enum.
        /// </summary>
        public static RadarColor? RadarColor
        {
            get
            {
                var raw = Math.Clamp(PropertyManager.GetLong("ml_digsite_radar_color", 3).Item, 0, byte.MaxValue);

                return raw == 0 ? (RadarColor?)null : (RadarColor)(byte)raw;
            }
        }

        // ---- creature confinement -----------------------------------------------------------------------

        /// <summary>
        /// Metres from the anchor inside which an encounter creature may hold a target (PropertyFloat 9009
        /// TetherRadius), with HomeRadius stamped at twice it. The same recipe MlRelariaSpawner and
        /// WorldEventSpawner.ApplyBossTether use, and for the same reason: a digsite sits in the open world,
        /// so without it one player can drag the whole fight across the island.
        /// </summary>
        public static double TetherRadius => PropertyManager.GetDouble("ml_digsite_boss_tether_radius", 40.0).Item;

        // ---- helpers ------------------------------------------------------------------------------------

        /// <summary>
        /// Seconds as a TimeSpan, with a negative value folded to zero. Zero is meaningful for the two wave
        /// clocks (it disables them) and harmless for the others, which are compared with a greater-than-zero
        /// guard before use.
        /// </summary>
        private static TimeSpan Seconds(long seconds)
            => TimeSpan.FromSeconds(Math.Clamp(seconds, 0, 86400));
    }
}
