using System;
using System.Collections.Generic;

using ACE.Server.WorldEvents.Defs;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// Decides which roster creatures physically FIT a given Thread dungeon, and projects the species tables
    /// the roster selector sees down to those that do (owner ruling: exclude by fit, never shrink the
    /// creature).
    ///
    /// The problem it solves: Threads places creatures into 13 curated dungeons at runtime, and seven of them
    /// are too tall for ten of those dungeons - a player stands across a doorway and kills a monster that can
    /// never close. Measured over 1040 creature-dungeon pairs, "movement height &lt;= the dungeon's
    /// maxCollisionHeightFullAccess" predicted pass/fail with ZERO disagreements, and radius never bound. So
    /// this is one height predicate, not a 13-column matrix, and the excluded set is identical across every
    /// tight dungeon.
    ///
    /// Pure and side-effect free by design, on the same contract as <see cref="DungeonSpellFilter"/>: no
    /// Creature, no Player, no DatManager, no DatabaseManager, no PropertyManager. The dat read and the
    /// tunable reads both live in <see cref="ThreadDungeonSpawner"/>, so every decision below is exercisable
    /// in a unit test with no dat and no database.
    ///
    /// THE MOVEMENT SEMANTICS ARE LOAD-BEARING. A mover's path envelope is Setup.Sphere ONLY, capped at the
    /// FIRST TWO SPHERES IN DECLARATION ORDER (the engine does not sort them), each scaled by the object's
    /// DefaultScale. Setup.CylSphere never widens the creature's own path. Verified against source by
    /// ENUMERATING every site that arms a path, rather than by any single call site:
    ///   - Source/ACE.Server/Physics/PhysicsObj.cs contains exactly SEVEN InitSphere calls - :1184 and :1186
    ///     (SetPosition), :2232 and :2234 (check_collision), and :4100, :4105 and :4107 (transition) - and
    ///     every one of them passes either PartArray.GetSphere() or PhysicsGlobals.DummySphere. NONE passes
    ///     GetCylSphere(). That enumeration, not the absence of a grep hit, is what makes the claim safe;
    ///   - Source/ACE.Server/Physics/Transition.cs:653-661 forwards both InitSphere overloads straight to
    ///     SpherePath;
    ///   - Source/ACE.Server/Physics/SpherePath.cs:106-119, InitSphere clamps NumSphere to 2 and multiplies
    ///     each sphere's Center and Radius by scale;
    ///   - Source/ACE.Server/Physics/PartArray.cs:231-234, GetSphere() returns Setup.Sphere verbatim.
    /// Cylspheres ARE read elsewhere in PhysicsObj, and none of those questions is this one: cell occupancy
    /// (calc_cross_cells, calc_cross_cells_static, obj_within_block), a radius accessor (GetPhysicsRadius),
    /// and object-versus-object collision (FindObjCollisions, and is_touching, which reads the OTHER object's
    /// PartArray). Those are named by METHOD rather than by line on purpose - the point is which QUESTION each
    /// one answers, and a line number here would be a maintenance burden that adds nothing to the argument.
    /// Never Setup.Radius, never Setup.Height, never a cylsphere.
    /// </summary>
    public static class DungeonFitFilter
    {
        /// <summary>
        /// The master switch's compile-time default: on. False reproduces today's behaviour EXACTLY - no
        /// creature is excluded, no projection is built, and no dat read is taken. Held here so
        /// <see cref="ACE.Server.Managers.PropertyManager"/>'s registration and the spawner's read can never
        /// drift apart, the same contract <see cref="DungeonSpellFilter.DefaultStripAoeSpellsEnabled"/> uses.
        /// </summary>
        public const bool DefaultFitFilterEnabled = true;

        /// <summary>
        /// Metres of extra headroom demanded on top of a creature's own movement height, default ZERO.
        ///
        /// Zero is the deliberate shipped value, not an unset one: it reproduces the measured verdict exactly
        /// (1040 pairs, zero disagreements). The knob exists because the clearance tool's own known gaps all
        /// err toward "too passable", so an admin who finds a survivor in the field can tighten the whole
        /// table by one number rather than waiting for a re-measure.
        /// </summary>
        public const double DefaultFitHeadroomMargin = 0.0;

        /// <summary>
        /// The typo ceiling on the margin. Two metres already excludes every creature from every shipped
        /// dungeon whose ceiling is under 2.2 m, so anything past it can only be a mistyped value.
        /// </summary>
        public const double MaxFitHeadroomMargin = 2.0;

        /// <summary>
        /// What the physics engine substitutes for an object with NO collision spheres: PhysicsGlobals's
        /// DummySphere is centred at z = 0.1 with radius 0.1 (Source/ACE.Server/Physics/PhysicsGlobals.cs:52-54),
        /// so its stack top is 0.20 m.
        ///
        /// UNSCALED is the right constant for the question THIS filter asks. Three sites install the dummy and
        /// they do NOT agree on scale: SetPosition (PhysicsObj.cs:1186) and transition (:4100) pass 1.0f,
        /// while check_collision (:2234) passes the object's own Scale. The first two are the sites that arm a
        /// MOVEMENT path - the doorway question - and both are unscaled; check_collision is an
        /// object-versus-object query and is not what decides whether a creature can walk down a corridor.
        ///
        /// It is also the fail-open value for "no sphere data": 0.20 m clears every dungeon in the table, so
        /// a creature whose rig cannot be measured is always eligible.
        /// </summary>
        public const float DummyMovementHeight = 0.20f;

        /// <summary>
        /// The cap the engine puts on how many spheres take part in a move: the first two, in DECLARATION
        /// ORDER (SpherePath.InitSphere clamps NumSphere to 2 and then indexes 0..NumSphere-1; it does not
        /// sort, so a third and taller sphere simply never participates in a transition).
        /// </summary>
        public const int MaxMovementSpheres = 2;

        /// <summary>
        /// The height of the body the physics engine actually moves through a doorway: the highest point of
        /// the first <see cref="MaxMovementSpheres"/> collision spheres, scaled.
        ///
        /// Returns <see cref="DummyMovementHeight"/> for a null or empty sphere list - that is what the engine
        /// itself substitutes, and it is also the fail-open answer, since 0.20 m fits everything.
        ///
        /// <paramref name="scale"/> multiplies BOTH the sphere origin and its radius, because InitSphere
        /// scales the whole sphere (Center * scale, Radius * scale), so the result is linear in scale. That
        /// linearity is what lets <see cref="ThreadDungeonSpawner"/> memoise one UNSCALED height per setup id
        /// and multiply it per weenie.
        /// </summary>
        public static float MovementHeight(IReadOnlyList<ACE.DatLoader.Entity.Sphere> spheres, float scale)
        {
            if (spheres == null || spheres.Count == 0)
                return DummyMovementHeight;

            var count = Math.Min(MaxMovementSpheres, spheres.Count);
            var top = float.MinValue;

            for (var i = 0; i < count; i++)
            {
                var sphere = spheres[i];

                if (sphere == null)
                    continue;

                var candidate = (sphere.Origin.Z + sphere.Radius) * scale;

                if (candidate > top)
                    top = candidate;
            }

            // Every one of the first two entries was null - the same "no usable sphere data" case as an empty
            // list, and it fails open the same way.
            return top == float.MinValue ? DummyMovementHeight : top;
        }

        /// <summary>
        /// Metres of slack allowed at the boundary, absorbing the float-to-double widening error in the
        /// comparison. NOT a tuning knob and not a second margin - one micrometre is far below any physically
        /// meaningful clearance, and <see cref="DefaultFitHeadroomMargin"/> is the knob for real headroom.
        ///
        /// It is required, not defensive. A movement height is a FLOAT (the dat stores float spheres and
        /// PartArray scales in float) and a clearance height is a DOUBLE parsed from JSON, so widening the
        /// float to compare them injects error: 2.95f widens to 2.950000047683716, which is strictly GREATER
        /// than the 2.95 parsed from clearance.json. Without this, a creature measuring exactly a dungeon's
        /// ceiling would be excluded by a rounding artefact roughly 5e-8 m wide. Same reasoning as the 1e-9
        /// epsilon on the boss level margin in DungeonPopulationBuilder, one scale up because this one absorbs
        /// a float mantissa rather than a double multiply.
        /// </summary>
        public const double FitEpsilon = 1e-6;

        /// <summary>
        /// The fit predicate: movementHeight + margin &lt;= dungeonHeight, inclusive at the boundary (see
        /// <see cref="FitEpsilon"/>).
        ///
        /// NO DATA MEANS NO CONSTRAINT. A <paramref name="dungeonHeight"/> at or below zero is "not measured"
        /// and every creature fits - which is also what an absent clearance row produces, because the caller
        /// never builds a predicate at all in that case.
        ///
        /// TOTAL FOR NaN, and deliberately fail-open: the comparison is written as the NEGATION of "too tall"
        /// (!(a &gt; b)) rather than as "short enough" (a &lt;= b), because every NaN comparison is false and
        /// the two forms therefore disagree on exactly that input. A NaN height must never empty a roster.
        /// </summary>
        public static bool Fits(float movementHeight, double dungeonHeight, double margin)
        {
            if (!(dungeonHeight > 0))
                return true;

            if (double.IsNaN(margin) || double.IsInfinity(margin))
                margin = DefaultFitHeadroomMargin;

            return !(movementHeight + margin > dungeonHeight + FitEpsilon);
        }

        /// <summary>The result of <see cref="Project"/>: the filtered tables plus what the filter cost.</summary>
        public readonly struct FitProjection
        {
            public FitProjection(IReadOnlyDictionary<string, SpeciesTableDef> species, int removedWcids, int emptiedFamilies)
            {
                Species = species;
                RemovedWcids = removedWcids;
                EmptiedFamilies = emptiedFamilies;
            }

            /// <summary>The filtered COPY. Never the dictionary that was passed in, even when nothing was removed.</summary>
            public IReadOnlyDictionary<string, SpeciesTableDef> Species { get; }

            /// <summary>How many member rows the predicate rejected, counted across every table.</summary>
            public int RemovedWcids { get; }

            /// <summary>How many tables went from at least one member to none. Such a table is KEPT, with an empty member list.</summary>
            public int EmptiedFamilies { get; }
        }

        /// <summary>
        /// A copy of <paramref name="species"/> whose member lists omit every wcid <paramref name="fits"/>
        /// rejects.
        ///
        /// THE TRAP: THIS MUST NEVER MUTATE ITS INPUT. The dictionary handed in is
        /// WorldEventManager.Store.SpeciesTables, which is live shared state read by World Events on other
        /// threads - stripping members in place would silently change what a world event can spawn, for the
        /// lifetime of the process, because of which dungeon somebody happened to open. So this allocates a
        /// NEW <see cref="SpeciesTableDef"/> and a NEW member list per family, and touches neither the
        /// original tables nor the original lists.
        ///
        /// Id AND CreatureType MUST SURVIVE THE COPY. Id is what DungeonRosterSelector.PickFamily orders and
        /// keys on and what ends up in DungeonSpawnPlan.FamilyId; CreatureType is half of the 3:1 dungeon
        /// family preference (PickFamily's WeightOf matches it against the dungeon's creatureTypes). Drop
        /// either and the preference weighting silently stops working and plan.FamilyId comes back null.
        /// The individual SpeciesMemberDef rows and the BiomeTags list are shared by REFERENCE rather than
        /// cloned. That is safe in BOTH directions and is not an oversight: nothing anywhere mutates a member
        /// row or a tag list, and the MEMBER LIST - the only collection this filter changes - is always a
        /// fresh one. Sharing a reference is not mutating it; what would break the input is editing a list it
        /// owns, and no line below does.
        ///
        /// A table left with NO members is kept in the dictionary with an empty member list rather than
        /// removed. That keeps a forced-family lookup resolving, so a gem naming an excluded family gets
        /// PickFamily's precise "no trash member in the level-N band" refusal instead of "unknown family".
        ///
        /// A null <paramref name="fits"/> means "no constraint" and copies everything through unchanged.
        /// </summary>
        public static FitProjection Project(IReadOnlyDictionary<string, SpeciesTableDef> species, Func<uint, bool> fits)
        {
            var projected = new Dictionary<string, SpeciesTableDef>();

            if (species == null)
                return new FitProjection(projected, 0, 0);

            var removedWcids = 0;
            var emptiedFamilies = 0;

            foreach (var kv in species)
            {
                var table = kv.Value;

                if (table == null)
                {
                    projected[kv.Key] = null;
                    continue;
                }

                var members = new List<SpeciesMemberDef>();
                var original = table.Members;

                if (original != null)
                {
                    foreach (var member in original)
                    {
                        if (member == null)
                            continue;

                        if (fits == null || fits(member.Wcid))
                            members.Add(member);
                        else
                            removedWcids++;
                    }
                }

                if (members.Count == 0 && (original?.Count ?? 0) > 0)
                    emptiedFamilies++;

                projected[kv.Key] = new SpeciesTableDef
                {
                    // Id and CreatureType are the two load-bearing fields; the rest are copied so the
                    // projection is indistinguishable from the original everywhere but the member list.
                    Id = table.Id,
                    DisplayName = table.DisplayName,
                    HueKey = table.HueKey,
                    BiomeTags = table.BiomeTags,
                    CreatureType = table.CreatureType,
                    Source = table.Source,
                    Members = members,
                };
            }

            return new FitProjection(projected, removedWcids, emptiedFamilies);
        }
    }
}
