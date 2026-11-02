using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Database;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldEvents;
using ACE.Server.WorldObjects;

using log4net;

namespace ACE.Server.Pinnacles
{
    /// <summary>
    /// Scales one freshly constructed encounter creature to the Knot's level (section 7 Q2), with the existing
    /// Threads / World Events band-uplift machinery and NO new formula. The reusable entry point is
    /// <see cref="BandUplift.Apply"/> (level floor, skill medians, body-part ratio) against the band standard
    /// <see cref="WorldEventBandMemo.StandardFor"/> computes for that level, then the band-median health floor
    /// (<see cref="WorldEventSpawner.HealthFloorTarget"/> / <see cref="WorldEventSpawner.ApplyHealth"/>): the same
    /// sequence <c>WorldEventSpawner.ApplyBandUplift</c> runs, which is private there. Threads itself has no
    /// stand-alone per-creature entry point (its scaling is interleaved with a whole-run plan inside
    /// ThreadDungeonSpawner.TryPlaceOnce).
    ///
    /// That uplift is UPWARD-ONLY. The TIER step that follows it (<see cref="PinnacleTier"/>, ApplyTier) is two-way:
    /// health is SET to a multiple of the Threads health curve T(L) and the level is SET, and a Boss or Head also takes
    /// the Threads boss normalization and boss ratings. No Threads name prefixes, XP pricing, group factors or
    /// combat-trait strip. Call it BEFORE EnterWorld. Never throws: a failure is logged and the creature spawns with
    /// whatever was written before the failure.
    /// </summary>
    public static class PinnacleScaling
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// WorldEventBandMemo is documented world-thread-only (unlocked Dictionary writes), but a fight spawns from its
        /// landblock thread under the parallel landblock tick and an entry warms it from a player thread. Every memo
        /// call goes through this one gate; BandUplift.Apply (the creature write) stays outside it.
        /// </summary>
        private static readonly object MemoGate = new object();

        /// <summary>
        /// Builds (and memoizes) the band standard and median health for <paramref name="knotLevel"/> under the gate.
        /// Called at entry, on the player thread, so the DB reads behind the profiles stay off the fight tick.
        /// Never throws.
        /// </summary>
        public static void Warm(int knotLevel)
        {
            if (knotLevel <= 0)
                return;

            try
            {
                BandFor(knotLevel);
            }
            catch (Exception ex)
            {
                log.Error($"[PINNACLE] could not warm the band memo for knot level {knotLevel}", ex);
            }
        }

        private static (DungeonBandStandard Standard, uint Median) BandFor(int knotLevel)
        {
            var tables = WorldEventHooks.Store?.SpeciesTables;
            var (low, high) = WorldEventRosterSelector.EffectiveTrashMultipliers();
            var dials = WorldEventRosterSelector.CurrentUpliftDials();

            lock (MemoGate)
            {
                var standard = WorldEventBandMemo.StandardFor(tables, knotLevel, low, high, dials.LowFloor, LiveProfileOf);
                var median = WorldEventBandMemo.MedianHealthFor(tables, knotLevel, low, high, LiveProfileOf);
                return (standard, median);
            }
        }

        /// <summary>
        /// The level to record as the creature's LootGateLevel: its AUTHORED level when the uplift will raise it, else
        /// null (leave it). Rare-drop eligibility reads the authored level (the World Events rule), so a Pinnacle corpse
        /// never rolls a rare off the Knot level; the reward is the cache. Pure.
        /// </summary>
        internal static int? LootGateFor(int? currentLevel, int knotLevel)
            => BandUplift.LevelFloor(currentLevel, knotLevel).HasValue ? currentLevel ?? 0 : (int?)null;

        // ================================================================ tiers (E2)

        /// <summary>Elite max health = this x the Threads health curve T(L) (owner ruling: a 1.5x boost; Threads Elite has no stat tier).</summary>
        public const double EliteHealthCurveMultiplier = 1.5;

        /// <summary>Boss max health = this x T(L).</summary>
        public const double BossHealthCurveMultiplier = 3.0;

        /// <summary>Head max health = this x the Boss health (so 4.5 x T(L)); its damage stays the Boss's.</summary>
        public const double HeadHealthOverBossMultiplier = 1.5;

        /// <summary>
        /// The Boss / Head DamageRating, SET after the authored ratings are zeroed: the figure the Threads spawner gives a
        /// normalized boss over a pack with no ratings of its own (DungeonPopulationLimits.DefaultBossDamageRatingFloor).
        /// Own literal so a Threads retune does not move it silently; PinnacleScalingTests pins the two equal.
        /// </summary>
        public const int BossDamageRating = 30;

        /// <summary>The Boss / Head DamageResistRating (DungeonPopulationLimits.DefaultBossDamageResistFloor), same rule.</summary>
        public const int BossDamageResistRating = 15;

        /// <summary>The tier's multiple of T(L). Pure.</summary>
        internal static double TierHealthFactor(PinnacleTier tier)
            => tier switch
            {
                PinnacleTier.Elite => EliteHealthCurveMultiplier,
                PinnacleTier.Boss => BossHealthCurveMultiplier,
                _ => BossHealthCurveMultiplier * HeadHealthOverBossMultiplier,
            };

        /// <summary>
        /// The tier's max health at <paramref name="knotLevel"/>: TierHealthFactor x DungeonHealthCurve.Target (which
        /// sanitizes the anchors and the top level itself), rounded, in [1, int.MaxValue]; 0 ("leave it") at a level of
        /// 0 or below. Two-way: may be lower than authored. Pure.
        /// </summary>
        internal static uint TierHealth(PinnacleTier tier, int knotLevel, double anchorLow, double anchorHigh, long topLevel)
        {
            if (knotLevel <= 0)
                return 0;

            var curve = DungeonHealthCurve.Target(knotLevel, anchorLow, anchorHigh, topLevel);

            if (!(curve > 0))
                return 0;

            return (uint)Math.Clamp(Math.Round(curve * TierHealthFactor(tier)), 1, int.MaxValue);
        }

        /// <summary>
        /// The tier's level: Elite = the Knot level; Boss and Head = the Threads boss stamp max(ceil(L x 1.10), L + 1)
        /// (DungeonPopulationBuilder's margin, with its 1e-9 guard so 100 x 1.10 reads 110, not 111). Pure.
        /// </summary>
        internal static int TierLevel(PinnacleTier tier, int knotLevel)
        {
            if (tier == PinnacleTier.Elite)
                return knotLevel;

            var stamped = Math.Ceiling(knotLevel * DungeonPopulationLimits.DefaultBossLevelMargin - 1e-9);

            return (int)Math.Clamp(Math.Max(stamped, knotLevel + 1.0), 1, int.MaxValue);
        }

        /// <summary>
        /// The synthetic plan DungeonCreatureNormalizer.NormalizeBoss reads: ONLY BandStandard, BossOffenseBandRatio and
        /// BossDefenseBandRatio (PinnacleScalingTests pins that set against the normalizer's source, so a Threads change
        /// that starts reading another field fails a test instead of reading a default here). Threads default ratios.
        /// </summary>
        internal static DungeonSpawnPlan BossPlan(DungeonBandStandard standard)
            => new DungeonSpawnPlan
            {
                BandStandard = standard ?? DungeonBandStandard.Empty,
                BossOffenseBandRatio = DungeonPopulationLimits.DefaultBossOffenseBandRatio,
                BossDefenseBandRatio = DungeonPopulationLimits.DefaultBossDefenseBandRatio,
            };

        /// <summary>
        /// The tier step, after the band uplift and weapon scaling. Boss / Head: NormalizeBossAttributes (the six attributes
        /// SET to the band medians, as the Threads spawner does before its health block), NormalizeBoss (skills, body
        /// parts, regen, weapons SET to the band standard), authored ratings zeroed, then the Boss ratings. Every tier:
        /// Level set to TierLevel, max health SET (two-way) to TierHealth through WorldEventSpawner.ApplyHealth, LAST, so
        /// the Endurance write above cannot move it (ApplyHealth measures its delta against the live MaxValue). Returns a
        /// short description for the log line.
        /// </summary>
        private static string ApplyTier(Creature creature, int knotLevel, PinnacleTier tier, DungeonBandStandard standard)
            => ApplyTier(creature, knotLevel, tier, standard, PinnacleTunables.HealthCurveDials());

        /// <summary>The tier step on explicit curve dials (the test seam: no settings read).</summary>
        internal static string ApplyTier(Creature creature, int knotLevel, PinnacleTier tier, DungeonBandStandard standard, (double Low, double High, long Top) dials)
        {
            var notes = new List<string>();

            if (tier != PinnacleTier.Elite)
            {
                var plan = BossPlan(standard);
                var attributesSet = DungeonCreatureNormalizer.NormalizeBossAttributes(creature, plan);
                var (skillsSet, partsChanged, regenSet, weaponsChanged) = DungeonCreatureNormalizer.NormalizeBoss(creature, plan);
                var zeroed = DungeonCreatureNormalizer.ZeroBossRatings(creature);

                creature.DamageRating = BossDamageRating;
                creature.DamageResistRating = BossDamageResistRating;

                notes.Add($"boss normalize {attributesSet} attribute(s) {skillsSet} skill(s) {partsChanged} part(s) {weaponsChanged} weapon(s) regen {(regenSet ? "set" : "authored")}, " +
                          $"{zeroed} rating(s) zeroed, dr {BossDamageRating} drr {BossDamageResistRating}");
            }

            creature.Level = TierLevel(tier, knotLevel);

            // Owner ruling 2026-10-20: Pinnacle tiers ALWAYS use the curve; the Threads master switch dynamic_dungeons_health_curve is not read.
            var health = TierHealth(tier, knotLevel, dials.Low, dials.High, dials.Top);

            if (health > 0 && WorldEventSpawner.ApplyHealth(creature, (int)health))
                log.Warn($"[PINNACLE] wcid {creature.WeenieClassId} {tier} health target {health} unreachable; StartingValue clamped to 1 (MaxValue={creature.Health.MaxValue})");

            notes.Add($"level {creature.Level}, health {(health > 0 ? health.ToString() : "unchanged")}");

            return string.Join("; ", notes);
        }

        /// <summary>
        /// Returns true when the scaling ran (even if the band had nothing to measure). <paramref name="knotLevel"/> 0 or
        /// below: nothing at all, the creature spawns as authored.
        /// </summary>
        public static bool Apply(Creature creature, int knotLevel, PinnacleTier tier)
        {
            if (creature == null || knotLevel <= 0)
                return false;

            try
            {
                var (standard, median) = BandFor(knotLevel);
                var authoredLevel = creature.Level ?? 0;

                // Rare eligibility reads the AUTHORED level: recorded BEFORE Apply raises Level.
                var gate = LootGateFor(creature.Level, knotLevel);

                if (gate.HasValue)
                    creature.LootGateLevel = gate.Value;

                // C6: the body-part damage ratio BandUplift.Apply is about to use, measured BEFORE it rewrites the parts
                var damageRatio = standard == null || standard.IsEmpty ? 1.0 : BodyDamageRatio(creature.Biota?.PropertiesBodyPart, standard.MaxBodyDamage);

                var (skillsRaised, partsScaled) = BandUplift.Apply(creature, knotLevel, standard);
                var weaponsScaled = ScaleWieldedWeapons(creature, damageRatio);
                var floorTarget = WorldEventSpawner.HealthFloorTarget(creature.Health.MaxValue, median);

                if (floorTarget > 0)
                    WorldEventSpawner.ApplyHealth(creature, (int)Math.Min(floorTarget, (uint)int.MaxValue));

                // AFTER the uplift, the weapon scaling and the floor: the tier's two-way SETs are what stand.
                var tierNotes = ApplyTier(creature, knotLevel, tier, standard);

                log.Info($"[PINNACLE] scaled wcid {creature.WeenieClassId} to knot level {knotLevel} as {tier} (authored {authoredLevel}): " +
                         $"{skillsRaised} skill(s) raised, {partsScaled} body part(s) scaled, {weaponsScaled} weapon(s) scaled x{damageRatio:0.###}, " +
                         $"health floor {(floorTarget > 0 ? floorTarget.ToString() : "none")}; {tierNotes}" +
                         (standard == null || standard.IsEmpty ? " (no band standard)" : ""));

                return true;
            }
            catch (Exception ex)
            {
                log.Error($"[PINNACLE] could not scale wcid {creature.WeenieClassId} to knot level {knotLevel}", ex);
                return false;
            }
        }

        /// <summary>
        /// The ratio <see cref="BandUplift.ApplyBodyPartUplift"/> scales body-part damage by: the creature's own maximum
        /// part DVal against the band standard's, upward only (<see cref="BandUplift.UpliftRatio"/>). Read BEFORE the
        /// uplift rewrites the parts. Pure.
        /// </summary>
        internal static double BodyDamageRatio(IDictionary<CombatBodyPart, PropertiesBodyPart> parts, uint standardMaxDamage)
        {
            var ownMax = 0;

            if (parts != null)
                foreach (var kvp in parts)
                    if (kvp.Value != null && kvp.Value.DVal > ownMax)
                        ownMax = kvp.Value.DVal;

            return BandUplift.UpliftRatio(ownMax, standardMaxDamage);
        }

        /// <summary>A wielded weapon's Damage after the body-part ratio: never lowered (<see cref="BandUplift.ScaleBodyValue"/>). Pure.</summary>
        internal static int ScaledWeaponDamage(int damage, double ratio) => BandUplift.ScaleBodyValue(damage, ratio);

        /// <summary>
        /// C6: a wielded weapon's Damage REPLACES the body part's DVal on the monster damage path, so a weapon-wielder
        /// keeps its authored hit unless the weapon climbs by the same ratio as the parts. Same item set and launcher
        /// exclusion as ReachUpStamp / DungeonCreatureNormalizer.NormalizeBossWeapons (a launcher contributes only its
        /// DamageMod to its ammunition). Per instance: every wielded item has its own biota, the weenie is never touched.
        /// Returns how many items were rewritten.
        /// </summary>
        private static int ScaleWieldedWeapons(Creature creature, double ratio)
        {
            if (!(ratio > 1.0))
                return 0;

            var scaled = 0;

            foreach (var item in creature.EquippedObjects.Values.Concat(creature.Inventory.Values).Where(i => i != null).Distinct())
            {
                if (item is MissileLauncher)
                    continue;

                var damage = item.GetProperty(PropertyInt.Damage) ?? 0;
                var next = ScaledWeaponDamage(damage, ratio);

                if (next == damage)
                    continue;

                item.SetProperty(PropertyInt.Damage, next);
                scaled++;
            }

            return scaled;
        }

        private static DungeonStatProfile LiveProfileOf(uint wcid)
            => DungeonStatProfile.FromWeenie(DatabaseManager.World.GetCachedWeenie(wcid));
    }
}
