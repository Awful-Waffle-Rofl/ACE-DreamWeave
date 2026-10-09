using System.Collections.Generic;

namespace ACE.Server.WorldEvents
{
    /// <summary>
    /// One candidate for the cleanup straggler sweep: an object standing on a landblock the run held,
    /// together with the guid of the generator that put it there.
    ///
    /// A plain named struct rather than a ValueTuple so the closure below can be driven from a unit test
    /// with no engine object anywhere near it (TECH-DESIGN D6).
    /// </summary>
    public readonly struct GeneratedCandidate
    {
        public GeneratedCandidate(uint guid, uint generatorId)
        {
            Guid = guid;
            GeneratorId = generatorId;
        }

        /// <summary>The candidate object's own guid.</summary>
        public uint Guid { get; }

        /// <summary>WorldObject.GeneratorId: the guid of the generator that spawned it.</summary>
        public uint GeneratorId { get; }
    }

    /// <summary>The outcome of one <see cref="WorldEventGeneratedAdds.ResolveStragglers"/> call.</summary>
    public readonly struct StragglerSweepResult
    {
        public StragglerSweepResult(IReadOnlyList<uint> guids, int passes, bool bounded)
        {
            Guids = guids;
            Passes = passes;
            Bounded = bounded;
        }

        /// <summary>Guids to destroy, in the order the closure matched them.</summary>
        public IReadOnlyList<uint> Guids { get; }

        /// <summary>How many passes the fixpoint took. Always at least 1.</summary>
        public int Passes { get; }

        /// <summary>
        /// True when the pass bound stopped the closure while it was still growing, i.e. the result may be
        /// incomplete. The caller logs this - it should never happen, because a generator chain that deep
        /// does not exist in any shipped roster.
        /// </summary>
        public bool Bounded { get; }
    }

    /// <summary>
    /// The decidable halves of the "generator-spawned add" fix, kept free of engine objects so they can be
    /// unit tested (TECH-DESIGN D6 - no test in ACE.Server.Tests builds a live Player, Session or Landblock).
    ///
    /// THE BUG this exists for: a roster weenie with generator profiles is itself a generator once
    /// WorldObjectFactory.CreateNewWorldObject builds it (both WorldObject constructors call
    /// InitializeGenerator), so an event-spawned monster quietly puts CHILDREN into the world on the
    /// landblock tick. Those children never pass through WorldEventSpawner.Adopt, so they are in no held
    /// list, and GeneratorDestruct.Undef/Nothing - which 82 of the 93 generator-bearing roster wcids carry -
    /// leaves them standing when the parent is destroyed. They then never rot and never unload, because
    /// events stage next to towns where players keep the block loaded.
    ///
    /// Two independent guards, and this class holds the pure core of both:
    ///   * <see cref="AdoptsGeneratedChild"/> - the birth-time gate (Task 1), evaluated from
    ///     WorldObject.OnGeneration for every generated object on the server;
    ///   * <see cref="ResolveStragglers"/> - the cleanup sweep (Task 2), the safety net for anything a
    ///     future code path leaks the same way, and for adds already orphaned by a parent a player killed
    ///     mid-run.
    /// </summary>
    public static class WorldEventGeneratedAdds
    {
        /// <summary>
        /// Pass bound for the transitive closure in <see cref="ResolveStragglers"/>. Generously above any
        /// real generator chain (a roster monster generating a child that itself generates a child is
        /// already unusual; three deep is not known to exist), and there purely so a cycle in the
        /// GeneratorId graph cannot spin a landblock action forever.
        /// </summary>
        public const int MaxSweepPasses = 8;

        /// <summary>
        /// The birth-time adoption decision, in its three terms.
        ///
        /// <paramref name="runActive"/> - a run exists and is still accepting held objects. This is the
        /// cheap bail: OnGeneration fires for EVERY generated object on the server (retail generators are
        /// everywhere), so the caller must decide this from a bare field read before it does anything else.
        ///
        /// <paramref name="generatorHeld"/> - the generator's own guid is one this run holds. Keyed on the
        /// GENERATOR's guid rather than on Creature.P_WorldEvent because P_WorldEvent lives on Creature
        /// only, and a generator child can be an item. It also makes adoption transitive for free: an
        /// adopted add is itself held, so its own children match on the next generation.
        ///
        /// <paramref name="childIsPlayer"/> - never, by construction (a Player is not generated), asserted
        /// anyway because the consequence of being wrong is destroying a player character.
        /// </summary>
        public static bool AdoptsGeneratedChild(bool runActive, bool generatorHeld, bool childIsPlayer)
        {
            return runActive && generatorHeld && !childIsPlayer;
        }

        /// <summary>
        /// Whether an adopted add is in a PLAYER's possession and must therefore survive cleanup.
        ///
        /// This exists because an add can be an item: a generator profile can spawn one, and a player may
        /// legitimately pick it up mid-run. Destroying it at Finish would take it out of their pack.
        /// Nothing else a run holds needs this - everything else is placed by the spawner and is a Creature
        /// or fixed scenery.
        ///
        /// <paramref name="hasOwner"/> is the container term, and it is OwnerId rather than ContainerId on
        /// purpose. Container.TryAddToInventory writes OwnerId, ContainerId and the live Container
        /// reference together, and TryRemoveFromInventory clears all three; every OTHER writer of
        /// ContainerId in the server is a vendor path, which writes the id and nothing else. So a
        /// ContainerId test would call a vendor's stock "possessed" and leave an event item in the world
        /// forever as purchasable stock, while an OwnerId test does not.
        ///
        /// <paramref name="containerIsVendor"/> is belt and braces on top of that, for a future path that
        /// sets both. <paramref name="isWielded"/> is the other half of possession: equipping writes
        /// WielderId and never touches OwnerId.
        /// </summary>
        public static bool IsPossessed(bool hasOwner, bool isWielded, bool containerIsVendor)
        {
            if (containerIsVendor)
                return false;

            return hasOwner || isWielded;
        }

        /// <summary>
        /// The distinct landblocks one run's cleanup sweep runs on, in the order it should visit them:
        /// the anchor, then every block the run HELD (the anchor's adjacents, which is exactly what the
        /// hold was computed from), then any block a held object actually stands on.
        ///
        /// The adjacents are load-bearing rather than tidy. A wave creature that died mid-run on an
        /// adjacent block has had its CurrentLandblock nulled by the landblock's own removal path, so if it
        /// was the last held object ever placed there, <paramref name="fromHeldObjects"/> would never name
        /// that block - and a straggler on it, exactly what the sweep exists for, would survive.
        ///
        /// Generic over the block type only so it can be driven with a stand-in from a unit test (D6);
        /// deduplication is by reference, which is what Landblock has (it overrides neither Equals nor
        /// GetHashCode).
        /// </summary>
        public static List<T> SweepTargets<T>(T anchor, IReadOnlyList<T> adjacents,
            IReadOnlyList<T> fromHeldObjects) where T : class
        {
            var blocks = new List<T>();

            if (anchor != null)
                blocks.Add(anchor);

            Absorb(blocks, adjacents);
            Absorb(blocks, fromHeldObjects);

            return blocks;
        }

        private static void Absorb<T>(List<T> blocks, IReadOnlyList<T> source) where T : class
        {
            if (source == null)
                return;

            for (var i = 0; i < source.Count; i++)
            {
                var block = source[i];

                if (block != null && !blocks.Contains(block))
                    blocks.Add(block);
            }
        }

        /// <summary>
        /// The transitive closure the cleanup sweep destroys.
        ///
        /// <paramref name="everHeld"/> is every guid the run EVER held - not what is still standing. That
        /// distinction is the whole point: by the time the sweep runs, DestroyAll has already queued the
        /// parents' destruction, and a destroyed parent must still match its orphans. WorldObject.Destroy
        /// nulls the DESTROYED object's own Generator/GeneratorId (WorldObject_Generators.NotifyOfEvent),
        /// never its children's, so a child's GeneratorId still names its dead parent.
        ///
        /// The closure is transitive because a grandchild's GeneratorId names the add, not the run's own
        /// spawn: once the add is matched it joins the owned set and the grandchild matches on the next
        /// pass. Bounded by <paramref name="maxPasses"/>; the result says whether the bound bit.
        ///
        /// Pure: no locks, no engine objects, no side effects on the inputs (<paramref name="everHeld"/> is
        /// copied before anything is added to it).
        /// </summary>
        public static StragglerSweepResult ResolveStragglers(IEnumerable<uint> everHeld,
            IReadOnlyList<GeneratedCandidate> candidates, int maxPasses = MaxSweepPasses)
        {
            var matched = new List<uint>();

            if (everHeld == null || candidates == null || candidates.Count == 0 || maxPasses < 1)
                return new StragglerSweepResult(matched, 0, false);

            var owned = new HashSet<uint>(everHeld);

            if (owned.Count == 0)
                return new StragglerSweepResult(matched, 0, false);

            // Working copy, so the caller's list is untouched and a matched candidate is not re-examined.
            var pending = new List<GeneratedCandidate>(candidates);

            var passes = 0;
            var grew = true;

            while (grew && passes < maxPasses)
            {
                grew = false;
                passes++;

                for (var i = pending.Count - 1; i >= 0; i--)
                {
                    var candidate = pending[i];

                    if (!owned.Contains(candidate.GeneratorId))
                        continue;

                    matched.Add(candidate.Guid);

                    // What makes the next pass find this candidate's own children.
                    owned.Add(candidate.Guid);

                    pending.RemoveAt(i);
                    grew = true;
                }
            }

            // grew is still true only when the bound stopped a pass that was still finding things.
            return new StragglerSweepResult(matched, passes, grew && passes >= maxPasses);
        }
    }
}
