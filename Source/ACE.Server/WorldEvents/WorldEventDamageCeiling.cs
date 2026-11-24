using System;
using System.Collections.Generic;

using ACE.Server.ThreadDungeons;
using ACE.Server.WorldEvents.Defs;
using ACE.Server.WorldObjects;

namespace ACE.Server.WorldEvents
{
    /// <summary>
    /// The Threads per-hit damage ceiling (<see cref="DamageCeiling"/>) for World Events (world_events_damage_ceiling,
    /// _ratio, _reference_ratio). Without it a WE creature uplifted to a slot level could out-hit a Threads creature of
    /// the same level, because Threads caps per-hit damage and WE did not.
    ///
    /// THE BASELINE is the Threads construction itself (<see cref="DungeonPopulationBuilder.DamageCeilingBaselineFor"/>:
    /// <see cref="DamageCeilingStandard.SpawnPool"/> then <see cref="DamageCeilingStandard.Compute"/>) over the WE species
    /// tables (the SAME tables Threads draws from, WorldEventHooks.Store) at the slot level, with the Threads COMPILED
    /// DEFAULT limits (<see cref="ThreadsLimits"/>), never a live dynamic_dungeons_* value - the rule the WE stat curve
    /// follows (WorldEventBandMemo's Curve* constants). So the WE median at level L is exactly the median a solo Threads
    /// gem at level L carries under the shipped dials, including above the authored top level, where Threads prices the
    /// pool by reach-up projection and the stat-curve stamp (DamageCeilingStandard.Predict).
    ///
    /// THE APPLICATION is <see cref="DamageCeiling.Apply"/> with no group bonus (WE has none): every melee and missile
    /// source compressed against the one shared median, base damage only, the rating untouched.
    ///
    /// Pure over its delegates; the spawner memoizes the baseline per (tables, slot level) in WorldEventBandMemo
    /// (world-thread writer only) and computes a new level through ONE WE-owned planning cache per species-table load
    /// (<see cref="ComputeLiveCached"/>), so levels share the archetype classification; /worldevent simulate computes it
    /// without either.
    /// </summary>
    public static class WorldEventDamageCeiling
    {
        /// <summary>
        /// The Threads limits at their compiled defaults. The five positional arguments (run size, reward caps, loot caps)
        /// are not read by the baseline; every field the baseline reads (band, floor, family counts, reach-up, stat curve
        /// and its rates, archetype uplift and its ratios, attribute raise) takes its compiled default. One exception:
        /// FamilyStretch is false here (the constructor's own default, not DefaultFamilyStretch), and the baseline never
        /// reads it anyway - the damage ceiling sample is native members only, so it must not move with the stretch switch.
        /// </summary>
        internal static readonly DungeonPopulationLimits ThreadsLimits = new DungeonPopulationLimits(60, DungeonPopulationLimits.DefaultXpCap,
            DungeonPopulationLimits.DefaultLumCap, DungeonGemSpec.MaxTier, 0.5,
            weaponAttackCurveRateAbove400: WorldEventBandMemo.CurveWeaponAttackRateAbove400,
            meleeDefenseCurveRateAbove400: WorldEventBandMemo.CurveMeleeDefenseRateAbove400);

        /// <summary>
        /// The gem's additive DamageRating the archetype prediction folds in: 0, a gem with no rating modifier (WE has no
        /// gem; damage-ceiling-census passes the same 0).
        /// </summary>
        internal const int GemDamageRating = 0;

        /// <summary>
        /// The baseline at <paramref name="level"/> (pure over the delegates): the Threads construction at the compiled
        /// default limits. <see cref="DamageCeilingBaseline.Empty"/> for no tables, no per-hit read or a level at or below 0.
        /// </summary>
        public static DamageCeilingBaseline Compute(IReadOnlyDictionary<string, SpeciesTableDef> tables, int level, Func<uint, int> levelOf,
            Func<uint, CreaturePerHit> perHitOf, Func<uint, DungeonStatProfile> profileOf, Func<uint, DungeonStatProfile> archetypeProfileOf)
        {
            if (tables == null || level <= 0 || levelOf == null || perHitOf == null)
                return DamageCeilingBaseline.Empty;

            var spec = new DungeonGemSpec(DungeonGemSpec.Any, level, DungeonGemSpec.MaxTier, DungeonGemSpec.Any, 0, null, 0, 0);

            return DungeonPopulationBuilder.DamageCeilingBaselineFor(spec, tables, levelOf, ThreadsLimits, perHitOf, profileOf, archetypeProfileOf,
                GemDamageRating, memo: null);
        }

        /// <summary>
        /// The same baseline through a planning cache (<paramref name="memo"/>, built by <see cref="NewPlanMemo"/>): every
        /// weenie read comes from the memo's own delegates, and the roster classification, per-wcid reads and band
        /// measurements are shared with every other level computed through the same memo. The result equals the
        /// memo-free <see cref="Compute(IReadOnlyDictionary{string, SpeciesTableDef}, int, Func{uint, int}, Func{uint, CreaturePerHit}, Func{uint, DungeonStatProfile}, Func{uint, DungeonStatProfile})"/>
        /// over those delegates (the Threads memo contract). The memo is written, so this is world thread only.
        /// </summary>
        public static DamageCeilingBaseline Compute(IReadOnlyDictionary<string, SpeciesTableDef> tables, int level, ThreadPlanMemo memo)
        {
            if (memo == null || !memo.HasPerHits)
                return DamageCeilingBaseline.Empty;

            if (tables == null || level <= 0)
                return DamageCeilingBaseline.Empty;

            var spec = new DungeonGemSpec(DungeonGemSpec.Any, level, DungeonGemSpec.MaxTier, DungeonGemSpec.Any, 0, null, 0, 0);

            return DungeonPopulationBuilder.DamageCeilingBaselineFor(spec, tables, memo.LevelOf, ThreadsLimits, memo.PerHitOf, memo.ProfileOf,
                memo.HasArchetypeProfiles ? memo.ArchetypeProfileOf : (Func<uint, DungeonStatProfile>)null, GemDamageRating, memo);
        }

        /// <summary>
        /// A WE-owned planning cache over the given weenie reads: a ThreadPlanMemo.Scratch, a private generation that never
        /// touches ThreadPlanCache's shared one or its switch. <paramref name="profileOf"/> is REQUIRED (null returns null:
        /// <see cref="Compute(IReadOnlyDictionary{string, SpeciesTableDef}, int, ThreadPlanMemo)"/> reads the memo's ProfileOf, which
        /// cannot run without one); <paramref name="archetypeProfileOf"/> may be null.
        /// </summary>
        public static ThreadPlanMemo NewPlanMemo(Func<uint, int> levelOf, Func<uint, CreaturePerHit> perHitOf, Func<uint, DungeonStatProfile> profileOf,
            Func<uint, DungeonStatProfile> archetypeProfileOf)
            => levelOf == null || perHitOf == null || profileOf == null ? null : ThreadPlanMemo.Scratch(levelOf, profileOf, archetypeProfileOf, perHitOf);

        /// <summary><see cref="NewPlanMemo"/> over the live weenie reads <see cref="ComputeLive(IReadOnlyDictionary{string, SpeciesTableDef}, int)"/> uses.</summary>
        // Known (code review R1, not fixed): LiveArchetypeProfileOf with the DEFAULT strip rebuilds the shared
        // ArchetypeFactsResolver whenever Threads' strip dials are non-default; perf only, a fix needs a Threads visibility change.
        public static ThreadPlanMemo NewLivePlanMemo()
            => NewPlanMemo(ThreadDungeonSpawner.LiveLevelOf, w => ThreadDungeonSpawner.LivePerHitOf(w, SpellStripConfig.Default),
                ThreadDungeonSpawner.LiveProfileOf, w => ThreadDungeonSpawner.LiveArchetypeProfileOf(w, SpellStripConfig.Default));

        /// <summary>
        /// <see cref="Compute"/> over the live weenie reads Threads uses (ThreadDungeonSpawner's Live* delegates, the
        /// spell strip at its compiled default). Writes no memo: safe for a command off the world thread.
        /// </summary>
        // Known (code review R1, not fixed): same default-strip LiveArchetypeProfileOf resolver rebuild as NewLivePlanMemo.
        public static DamageCeilingBaseline ComputeLive(IReadOnlyDictionary<string, SpeciesTableDef> tables, int level)
            => Compute(tables, level, ThreadDungeonSpawner.LiveLevelOf, w => ThreadDungeonSpawner.LivePerHitOf(w, SpellStripConfig.Default),
                ThreadDungeonSpawner.LiveProfileOf, w => ThreadDungeonSpawner.LiveArchetypeProfileOf(w, SpellStripConfig.Default));

        /// <summary>
        /// The spawner's read: <see cref="ComputeLive(IReadOnlyDictionary{string, SpeciesTableDef}, int)"/> through the
        /// WE-owned planning cache for these tables (WorldEventBandMemo.DamageCeilingPlanMemoFor). World thread only.
        /// </summary>
        public static DamageCeilingBaseline ComputeLiveCached(IReadOnlyDictionary<string, SpeciesTableDef> tables, int level)
            => Compute(tables, level, WorldEventBandMemo.DamageCeilingPlanMemoFor(tables, NewLivePlanMemo));

        /// <summary>
        /// The cap and the reference ratio top for a baseline under the (sanitized) dials, exactly as
        /// DungeonPopulationBuilder sets plan.DamageCeilingCap and plan.DamageCeilingTop: top is 0 with no median.
        /// </summary>
        public static (double Cap, double Top) CapAndTop(DamageCeilingBaseline baseline, WorldEventUpliftDials dials)
            => (DamageCeiling.SanitizeCap(dials.DamageCeilingRatio),
                baseline != null && baseline.Median > 0 ? baseline.TopFor(DamageCeiling.SanitizeReferenceRatio(dials.DamageCeilingReferenceRatio)) : 0.0);

        /// <summary>
        /// Whether the ceiling runs on a spawn (pure): the dial on, a known slot level (above 0), and a creature the WE
        /// combat guard covers (<see cref="WorldEventCombatGuard.AppliesTo"/>: a wave monster or a non-named boss). The
        /// named boss is exempt.
        /// </summary>
        public static bool Applies(WorldEventUpliftDials dials, WorldEventSpawnKind kind, bool isNamedBoss, int slotLevel)
            => dials.DamageCeiling && slotLevel > 0 && WorldEventCombatGuard.AppliesTo(kind, isNamedBoss);

        /// <summary>
        /// One per-hit after the ceiling (pure): DamageCeiling.FinalPerHit with no group bonus, i.e. the value a Threads
        /// solo creature with the same per-hit would be held to at the same median, cap and top.
        /// </summary>
        public static double PerHitAfter(double perHit, DamageCeilingBaseline baseline, WorldEventUpliftDials dials)
        {
            if (baseline == null || !(baseline.Median > 0))
                return perHit;

            var (cap, top) = CapAndTop(baseline, dials);

            return DamageCeiling.FinalPerHit(perHit, baseline.Median, cap, top, 0, 0);
        }

        /// <summary>
        /// Applies the ceiling to a live creature before EnterWorld (DamageCeiling.Apply, group bonus 0, R the creature's
        /// own DamageRating). Null, and nothing written, with the dial off or a baseline with no median.
        /// </summary>
        internal static DamageCeilingResult Apply(Creature creature, DamageCeilingBaseline baseline, WorldEventUpliftDials dials)
        {
            if (creature == null || !dials.DamageCeiling || baseline == null || !(baseline.Median > 0))
                return null;

            var (cap, top) = CapAndTop(baseline, dials);
            var ownRating = creature.DamageRating ?? 0;

            return DamageCeiling.Apply(creature, baseline.Median, cap, top, ownRating, 0);
        }
    }
}
