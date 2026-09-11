using System.Collections.Generic;
using System.Linq;

using ACE.Server.WorldEvents;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Unit coverage for the generator-spawned "add" fix (2026-09-05).
    ///
    /// THE BUG: a roster weenie carrying generator profiles is itself a generator once the factory builds
    /// it, so an event-spawned monster puts children into the world on the landblock tick. Those children
    /// never pass through WorldEventSpawner.Adopt, so cleanup never sees them, and the GeneratorDestruct
    /// value most of the affected roster weenies carry leaves them standing when the parent is destroyed.
    ///
    /// Only the decidable parts are exercised (TECH-DESIGN D6): no test in this suite builds a live Player,
    /// Session, Landblock or WorldObject, so the birth-time gate and the sweep closure are pure statics and
    /// those are what is tested here. The actual adoption and destruction need a live landblock and go to
    /// the verify queue, not to CI.
    /// </summary>
    [TestClass]
    public class WorldEventGeneratedAddsTests
    {
        private const uint ParentGuid = 0x80000010;
        private const uint ChildGuid = 0x80000011;
        private const uint GrandchildGuid = 0x80000012;
        private const uint StrangerGuid = 0x80000020;
        private const uint StrangerGeneratorGuid = 0x70000001;

        private static List<GeneratedCandidate> Candidates(params (uint Guid, uint GeneratorId)[] pairs)
        {
            return pairs.Select(p => new GeneratedCandidate(p.Guid, p.GeneratorId)).ToList();
        }

        // ---- the birth-time gate ------------------------------------------------------------------------

        /// <summary>
        /// All eight combinations. The gate adopts on exactly one of them, and the childIsPlayer term is a
        /// veto rather than a contributor - a Player can never be generated, and the assertion exists
        /// because the cost of being wrong is a player character in a run's destroy list.
        /// </summary>
        [TestMethod]
        public void AdoptsGeneratedChild_OnlyWhenARunIsUpAndItOwnsTheGenerator()
        {
            Assert.IsTrue(WorldEventGeneratedAdds.AdoptsGeneratedChild(true, true, false));

            Assert.IsFalse(WorldEventGeneratedAdds.AdoptsGeneratedChild(false, true, false), "no run is up");
            Assert.IsFalse(WorldEventGeneratedAdds.AdoptsGeneratedChild(true, false, false), "some other generator");
            Assert.IsFalse(WorldEventGeneratedAdds.AdoptsGeneratedChild(false, false, false));

            Assert.IsFalse(WorldEventGeneratedAdds.AdoptsGeneratedChild(true, true, true), "never a Player");
            Assert.IsFalse(WorldEventGeneratedAdds.AdoptsGeneratedChild(true, false, true));
            Assert.IsFalse(WorldEventGeneratedAdds.AdoptsGeneratedChild(false, true, true));
            Assert.IsFalse(WorldEventGeneratedAdds.AdoptsGeneratedChild(false, false, true));
        }

        // ---- the sweep closure --------------------------------------------------------------------------

        [TestMethod]
        public void ResolveStragglers_EmptyInputs_MatchNothing()
        {
            var none = WorldEventGeneratedAdds.ResolveStragglers(null, Candidates((ChildGuid, ParentGuid)));
            Assert.AreEqual(0, none.Guids.Count);
            Assert.IsFalse(none.Bounded);

            var noCandidates = WorldEventGeneratedAdds.ResolveStragglers(new[] { ParentGuid }, null);
            Assert.AreEqual(0, noCandidates.Guids.Count);

            var emptyCandidates = WorldEventGeneratedAdds.ResolveStragglers(new[] { ParentGuid },
                new List<GeneratedCandidate>());
            Assert.AreEqual(0, emptyCandidates.Guids.Count);

            var emptyOwned = WorldEventGeneratedAdds.ResolveStragglers(new uint[0],
                Candidates((ChildGuid, ParentGuid)));
            Assert.AreEqual(0, emptyOwned.Guids.Count);
        }

        /// <summary>
        /// The predicate itself: GeneratorId names a guid the run ever held. The parent being long since
        /// destroyed changes nothing, which is the whole reason the set is ever-held rather than
        /// still-standing - by the time the sweep runs, DestroyAll has already queued every parent's
        /// destruction.
        /// </summary>
        [TestMethod]
        public void ResolveStragglers_MatchesADirectChildOfAHeldGenerator()
        {
            var result = WorldEventGeneratedAdds.ResolveStragglers(new[] { ParentGuid },
                Candidates((ChildGuid, ParentGuid)));

            CollectionAssert.AreEqual(new[] { ChildGuid }, result.Guids.ToArray());
            Assert.IsFalse(result.Bounded);
        }

        /// <summary>
        /// No false positives. A generator the run never held is somebody else's - a retail encounter
        /// standing on the same landblock - and destroying its children would be a live content bug far
        /// worse than the leak.
        /// </summary>
        [TestMethod]
        public void ResolveStragglers_IgnoresChildrenOfGeneratorsTheRunNeverHeld()
        {
            var result = WorldEventGeneratedAdds.ResolveStragglers(new[] { ParentGuid },
                Candidates((StrangerGuid, StrangerGeneratorGuid)));

            Assert.AreEqual(0, result.Guids.Count);
        }

        /// <summary>
        /// The held generator is itself never a match: it is in the owned set as a GENERATOR, and only a
        /// candidate's GeneratorId is ever tested against that set.
        /// </summary>
        [TestMethod]
        public void ResolveStragglers_NeverMatchesTheHeldGeneratorItself()
        {
            // The parent is itself generated, by something the run does not own.
            var result = WorldEventGeneratedAdds.ResolveStragglers(new[] { ParentGuid },
                Candidates((ParentGuid, StrangerGeneratorGuid)));

            Assert.AreEqual(0, result.Guids.Count);
        }

        /// <summary>
        /// Transitivity, in the order that needs more than one pass: the grandchild appears BEFORE the
        /// child in the reverse scan, so it cannot match until the child has joined the owned set.
        /// </summary>
        [TestMethod]
        public void ResolveStragglers_IsTransitive_AcrossPasses()
        {
            var result = WorldEventGeneratedAdds.ResolveStragglers(new[] { ParentGuid },
                Candidates((ChildGuid, ParentGuid), (GrandchildGuid, ChildGuid)));

            CollectionAssert.AreEquivalent(new[] { ChildGuid, GrandchildGuid }, result.Guids.ToArray());
            Assert.IsTrue(result.Passes >= 2, $"a two-deep chain in this order needs a second pass, took {result.Passes}");
            Assert.IsFalse(result.Bounded);
        }

        /// <summary>
        /// The same chain in the other list order resolves inside a single pass, because the reverse scan
        /// reaches the child before the grandchild and the owned set grows as it goes.
        /// </summary>
        [TestMethod]
        public void ResolveStragglers_IsTransitive_WithinOnePassWhenTheOrderAllowsIt()
        {
            var result = WorldEventGeneratedAdds.ResolveStragglers(new[] { ParentGuid },
                Candidates((GrandchildGuid, ChildGuid), (ChildGuid, ParentGuid)));

            CollectionAssert.AreEquivalent(new[] { ChildGuid, GrandchildGuid }, result.Guids.ToArray());
            Assert.IsFalse(result.Bounded);
        }

        /// <summary>
        /// The bound is reported, not silently swallowed. One pass cannot resolve a two-deep chain in this
        /// order, so the closure stops with the grandchild still unmatched and says so.
        /// </summary>
        [TestMethod]
        public void ResolveStragglers_ReportsWhenThePassBoundStopsItEarly()
        {
            var result = WorldEventGeneratedAdds.ResolveStragglers(new[] { ParentGuid },
                Candidates((ChildGuid, ParentGuid), (GrandchildGuid, ChildGuid)), maxPasses: 1);

            CollectionAssert.AreEqual(new[] { ChildGuid }, result.Guids.ToArray());
            Assert.AreEqual(1, result.Passes);
            Assert.IsTrue(result.Bounded, "the caller logs this; it must not resolve silently short");
        }

        /// <summary>
        /// A cycle in the GeneratorId graph terminates instead of spinning a landblock action forever, and
        /// - because neither guid is owned - matches nothing.
        /// </summary>
        [TestMethod]
        public void ResolveStragglers_TerminatesOnACycleAndMatchesNothing()
        {
            var result = WorldEventGeneratedAdds.ResolveStragglers(new[] { ParentGuid },
                Candidates((ChildGuid, GrandchildGuid), (GrandchildGuid, ChildGuid)));

            Assert.AreEqual(0, result.Guids.Count);
            Assert.IsTrue(result.Passes <= WorldEventGeneratedAdds.MaxSweepPasses);
            Assert.IsFalse(result.Bounded);
        }

        /// <summary>
        /// Pure: the caller hands the same ever-held set to one sweep per landblock, so the closure must
        /// not write into it (each block gets its own copy in the live path, but the guarantee belongs
        /// here).
        /// </summary>
        [TestMethod]
        public void ResolveStragglers_DoesNotMutateItsInputs()
        {
            var owned = new HashSet<uint> { ParentGuid };
            var candidates = Candidates((ChildGuid, ParentGuid), (GrandchildGuid, ChildGuid));

            WorldEventGeneratedAdds.ResolveStragglers(owned, candidates);

            Assert.AreEqual(1, owned.Count, "the owned set must be copied, not grown in place");
            Assert.IsTrue(owned.Contains(ParentGuid));
            Assert.AreEqual(2, candidates.Count, "the candidate list must be copied, not drained");
        }

        // ---- kind rules ---------------------------------------------------------------------------------

        /// <summary>
        /// Ordinals, re-derived from WorldEventSpawnKind in this worktree. Add is appended LAST on purpose:
        /// inserting it anywhere else would renumber the existing members.
        /// </summary>
        [TestMethod]
        public void SpawnKind_AddIsAppendedLast_AndTheExistingOrdinalsAreUnchanged()
        {
            // Through a method rather than a direct cast, so the comparison is not a compile-time constant
            // the MSTEST0032 analyzer flags as always-true.
            static int Ordinal(WorldEventSpawnKind kind) => (int)kind;

            Assert.AreEqual(0, Ordinal(WorldEventSpawnKind.Wave));
            Assert.AreEqual(1, Ordinal(WorldEventSpawnKind.Source));
            Assert.AreEqual(2, Ordinal(WorldEventSpawnKind.Boss));
            Assert.AreEqual(3, Ordinal(WorldEventSpawnKind.Decor));
            Assert.AreEqual(4, Ordinal(WorldEventSpawnKind.Npc));
            Assert.AreEqual(5, Ordinal(WorldEventSpawnKind.InertObjective));
            Assert.AreEqual(6, Ordinal(WorldEventSpawnKind.Add));
        }

        /// <summary>
        /// The load-bearing rule: an add is held for cleanup and counts for NOTHING else. Taking the
        /// not-live side is what keeps it out of liveWave, MaxAlive, sourceGuids, the pace controller, the
        /// throughput ledger and the kill objective - the run was composed without it, and a monster that
        /// spawns its own escort must not inflate the numbers the difficulty controller steers by.
        /// </summary>
        [TestMethod]
        public void CountsAsLive_IsFalseForAdd_AndUnchangedForEveryOtherKind()
        {
            Assert.IsFalse(WorldEventSpawner.CountsAsLive(WorldEventSpawnKind.Add));

            Assert.IsTrue(WorldEventSpawner.CountsAsLive(WorldEventSpawnKind.Wave));
            Assert.IsTrue(WorldEventSpawner.CountsAsLive(WorldEventSpawnKind.Source));
            Assert.IsTrue(WorldEventSpawner.CountsAsLive(WorldEventSpawnKind.Boss));
            Assert.IsFalse(WorldEventSpawner.CountsAsLive(WorldEventSpawnKind.Decor));
            Assert.IsFalse(WorldEventSpawner.CountsAsLive(WorldEventSpawnKind.Npc));
            Assert.IsFalse(WorldEventSpawner.CountsAsLive(WorldEventSpawnKind.InertObjective));
        }

        /// <summary>An add is never wave pressure, so it can never pull another wave forward.</summary>
        [TestMethod]
        public void CountsAsWavePressure_IsFalseForAdd()
        {
            Assert.IsFalse(WorldEventSpawner.CountsAsWavePressure(WorldEventSpawnKind.Add));
            Assert.IsTrue(WorldEventSpawner.CountsAsWavePressure(WorldEventSpawnKind.Wave));
        }

        /// <summary>
        /// Nothing PLACES an add, so it never reaches the creature path this guards - and what a generator
        /// produced is not ours to require anything of: it can be an item as easily as a monster, which is
        /// exactly why the adoption is keyed on the generator's guid rather than on Creature.P_WorldEvent.
        /// </summary>
        [TestMethod]
        public void RequiresCreature_IsFalseForAdd_AndUnchangedForEveryOtherKind()
        {
            Assert.IsFalse(WorldEventSpawner.RequiresCreature(WorldEventSpawnKind.Add));
            Assert.IsFalse(WorldEventSpawner.RequiresCreature(WorldEventSpawnKind.Decor));

            Assert.IsTrue(WorldEventSpawner.RequiresCreature(WorldEventSpawnKind.Wave));
            Assert.IsTrue(WorldEventSpawner.RequiresCreature(WorldEventSpawnKind.Source));
            Assert.IsTrue(WorldEventSpawner.RequiresCreature(WorldEventSpawnKind.Boss));
            Assert.IsTrue(WorldEventSpawner.RequiresCreature(WorldEventSpawnKind.Npc));
            Assert.IsTrue(WorldEventSpawner.RequiresCreature(WorldEventSpawnKind.InertObjective));
        }

        /// <summary>
        /// DestroyedByPass is unchanged by this work: it dispatches on "is this a cache", not on the kind.
        /// An add carries no PropertyBool.WorldEventCache (nothing stamps one on it), so it is on the
        /// non-cache side and is destroyed by BOTH passes - the creature pass at Finish clears the field,
        /// the final sweep is the safety net.
        /// </summary>
        [TestMethod]
        public void DestroyedByPass_TakesTheNonCacheSideForAnAdd()
        {
            Assert.IsTrue(WorldEventSpawner.DestroyedByPass(isCache: false, includeCaches: false));
            Assert.IsTrue(WorldEventSpawner.DestroyedByPass(isCache: false, includeCaches: true));

            Assert.IsFalse(WorldEventSpawner.DestroyedByPass(isCache: true, includeCaches: false));
            Assert.IsTrue(WorldEventSpawner.DestroyedByPass(isCache: true, includeCaches: true));
        }

        // ---- spawner bookkeeping ------------------------------------------------------------------------

        [TestMethod]
        public void FreshSpawner_ReportsNoAdds()
        {
            var spawner = new WorldEventSpawner();

            Assert.AreEqual(0, spawner.AddCount);
            Assert.IsFalse(spawner.IsAddGuid(ChildGuid));

            var counters = spawner.AddCounters;

            Assert.AreEqual(0, counters.Adopted);
            Assert.AreEqual(0, counters.Stray);

            // An add must not have leaked into any of the sets the run steers by.
            Assert.AreEqual(0, spawner.LiveCount);
            Assert.AreEqual(0, spawner.LiveTotal);
            Assert.AreEqual(0, spawner.DecorCount);
            Assert.AreEqual(0, spawner.NpcCount);
            Assert.IsFalse(spawner.IsDecorGuid(ChildGuid));
            Assert.IsFalse(spawner.IsNpcGuid(ChildGuid));
        }

        /// <summary>
        /// The one thing an add needs that no other held kind does. A generator profile can spawn an ITEM
        /// and a player may legitimately pick it up mid-run; cleanup must not then take it out of their
        /// pack. A null is not "taken" - the destroy loop has its own null guard ahead of this.
        /// </summary>
        [TestMethod]
        public void IsPlayerTaken_IsFalseForNull()
        {
            Assert.IsFalse(WorldEventSpawner.IsPlayerTaken(null));
        }

        /// <summary>
        /// All eight combinations of the possession decision. An add on the ground is destroyed; one in a
        /// player's pack or on their person is not.
        /// </summary>
        [TestMethod]
        public void IsPossessed_IsTrueOnlyForAnOwnerOrAWielder()
        {
            Assert.IsFalse(WorldEventGeneratedAdds.IsPossessed(false, false, false), "standing on the ground");

            Assert.IsTrue(WorldEventGeneratedAdds.IsPossessed(true, false, false), "in a pack");
            Assert.IsTrue(WorldEventGeneratedAdds.IsPossessed(false, true, false), "wielded");
            Assert.IsTrue(WorldEventGeneratedAdds.IsPossessed(true, true, false));
        }

        /// <summary>
        /// A vendor sale is not possession. Container.TryAddToInventory writes OwnerId, ContainerId and the
        /// live Container reference together and TryRemoveFromInventory clears all three, but a vendor sale
        /// runs after that removal and writes ContainerId back ALONE - so an add a player sold has no owner
        /// and no wielder, and reading ContainerId instead would have called it possessed forever and left
        /// an event item in the economy as permanent, purchasable stock.
        ///
        /// The explicit vendor term is the belt-and-braces half: it vetoes even when an owner IS set, for a
        /// future path that writes both.
        /// </summary>
        [TestMethod]
        public void IsPossessed_IsFalseForVendorStock()
        {
            // What a vendor sale actually leaves behind: no owner, no wielder.
            Assert.IsFalse(WorldEventGeneratedAdds.IsPossessed(false, false, true));

            // And the veto, for a path that sets an owner as well.
            Assert.IsFalse(WorldEventGeneratedAdds.IsPossessed(true, false, true), "a vendor is never a player");
            Assert.IsFalse(WorldEventGeneratedAdds.IsPossessed(false, true, true));
            Assert.IsFalse(WorldEventGeneratedAdds.IsPossessed(true, true, true));
        }

        // ---- the sweep's landblock set ------------------------------------------------------------------

        /// <summary>Stand-in for Landblock: a reference type overriding neither Equals nor GetHashCode.</summary>
        private sealed class FakeBlock
        {
            public FakeBlock(string name) => Name = name;

            public string Name { get; }

            public override string ToString() => Name;
        }

        /// <summary>
        /// The hole this closes: a wave creature that died mid-run on an ADJACENT held block has had its
        /// CurrentLandblock nulled by the landblock's removal path, so if it was the last held object ever
        /// placed there, the held-object scan names only the anchor - and a straggler on the adjacent block
        /// survives. The adjacents must be swept whether or not anything is still standing on them.
        /// </summary>
        [TestMethod]
        public void SweepTargets_IncludesTheAdjacents_EvenWhenNoHeldObjectStandsOnThem()
        {
            var anchor = new FakeBlock("anchor");
            var adjacent = new FakeBlock("adjacent");

            var targets = WorldEventGeneratedAdds.SweepTargets(anchor, new[] { adjacent }, new FakeBlock[0]);

            CollectionAssert.AreEqual(new[] { anchor, adjacent }, targets, "the anchor is swept first, then the held adjacents");
        }

        [TestMethod]
        public void SweepTargets_DeduplicatesByReference_AcrossAllThreeSources()
        {
            var anchor = new FakeBlock("anchor");
            var adjacent = new FakeBlock("adjacent");
            var stray = new FakeBlock("stray");

            var targets = WorldEventGeneratedAdds.SweepTargets(anchor,
                new[] { adjacent, anchor, adjacent },
                new[] { anchor, adjacent, stray, stray });

            CollectionAssert.AreEqual(new[] { anchor, adjacent, stray }, targets);
        }

        /// <summary>
        /// A held object can stand on a block that is neither the anchor nor an adjacent - a spawn offset
        /// that crossed a boundary - and that block must still be swept.
        /// </summary>
        [TestMethod]
        public void SweepTargets_KeepsABlockKnownOnlyFromAHeldObject()
        {
            var anchor = new FakeBlock("anchor");
            var elsewhere = new FakeBlock("elsewhere");

            var targets = WorldEventGeneratedAdds.SweepTargets(anchor, new FakeBlock[0], new[] { elsewhere });

            CollectionAssert.AreEqual(new[] { anchor, elsewhere }, targets);
        }

        /// <summary>
        /// A run that never took a landblock hold (no bridge, or the hold failed) sweeps nothing, and a
        /// null in any source list is dropped rather than carried into the sweep.
        /// </summary>
        [TestMethod]
        public void SweepTargets_ToleratesNullsAndAMissingAnchor()
        {
            Assert.AreEqual(0, WorldEventGeneratedAdds.SweepTargets<FakeBlock>(null, null, null).Count);

            var elsewhere = new FakeBlock("elsewhere");

            var targets = WorldEventGeneratedAdds.SweepTargets<FakeBlock>(null,
                new FakeBlock[] { null }, new[] { null, elsewhere });

            CollectionAssert.AreEqual(new[] { elsewhere }, targets);
        }

        // ---- the held-list contract the adoption depends on ---------------------------------------------

        /// <summary>
        /// No composition, no request, no objective and no landblock bridge: this run can hold nothing and
        /// spawn nothing, which is exactly the surface the held-list contract lives on (D6).
        /// </summary>
        private static WorldEvent BareEvent()
        {
            return new WorldEvent(1, null, null, null, null, () => 0.0);
        }

        [TestMethod]
        public void IsHeld_ReadsTheEverHeldGuidSet()
        {
            var evt = BareEvent();

            Assert.IsFalse(evt.IsHeld(ParentGuid), "a fresh run holds nothing");

            // AddHeld needs a live WorldObject, which D6 forbids a test from building, so the set it
            // maintains is driven directly - HeldGuids is public and guarded by the same monitor IsHeld
            // takes.
            evt.HeldGuids.Add(ParentGuid);

            Assert.IsTrue(evt.IsHeld(ParentGuid));
            Assert.IsFalse(evt.IsHeld(StrangerGeneratorGuid));
        }

        /// <summary>
        /// The set must stay EVER-held: closing the list is what stops new adoptions, and it must not
        /// forget what the run already owned. The cleanup sweep keys on this set at a point where every
        /// parent has already been queued for destruction, so a set that shrank would match nothing.
        /// </summary>
        [TestMethod]
        public void CloseHeld_StopsNewAdoptions_ButDoesNotForgetWhatWasHeld()
        {
            var evt = BareEvent();

            evt.HeldGuids.Add(ParentGuid);

            Assert.IsFalse(evt.HeldClosed);

            evt.CloseHeld();

            Assert.IsTrue(evt.HeldClosed);
            Assert.IsFalse(evt.AddHeld(null), "a closed list accepts nothing");
            Assert.IsTrue(evt.IsHeld(ParentGuid), "ever-held, not still-standing");
        }

        [TestMethod]
        public void HeldGuidsSnapshot_IsACopy()
        {
            var evt = BareEvent();

            evt.HeldGuids.Add(ParentGuid);

            var snapshot = evt.HeldGuidsSnapshot();

            Assert.AreEqual(1, snapshot.Count);
            Assert.IsTrue(snapshot.Contains(ParentGuid));

            // Each landblock's sweep mutates its own copy as the closure grows; aliasing the live set
            // would have two landblock threads writing one HashSet.
            snapshot.Add(ChildGuid);

            Assert.AreEqual(1, evt.HeldGuids.Count, "the snapshot must not alias HeldGuids");
            Assert.IsFalse(evt.IsHeld(ChildGuid));
        }
    }
}
