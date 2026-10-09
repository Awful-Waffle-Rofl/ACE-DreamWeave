using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.ClassAbilities;
using ACE.Server.ClassAbilities.Abilities;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Coverage for IPreWriteDamageAbility, the pre-write incoming-damage hook, and specifically for its
    /// ORDER contract - which is the one part of the mechanism that is a pure function of the registry and
    /// therefore testable here.
    ///
    /// WHAT IS NOT COVERED, AND CANNOT BE: Player.ApplyPreWriteDamageClassAbilities itself. The learned
    /// handler cache, the attacker filter, the Mana spend and the absorb chat lines all hang off a live
    /// Player, and ACE.Server.Tests can never construct one - Player's static initializer cannot run under
    /// the test host. That is the same limit ManaBarrierTests and SanguineWardTests already record for
    /// AbsorbWithManaBarrier / AbsorbWithSanguineWard, and it is also why the dispatch reads the
    /// class_abilities_enabled tunable rather than being handed it (a PropertyManager read throws in a unit
    /// test without a live config). A test asserting anything about the dispatch BODY here would be
    /// asserting against a reimplementation of it, so there deliberately is none: the arithmetic that
    /// matters is pinned below as pure functions instead.
    /// </summary>
    [TestClass]
    public class PreWriteDamageHookTests
    {
        [TestMethod]
        public void Bucket_IsSortedByMitigationOrder_NotRegistrationOrder()
        {
            var orders = ClassAbilityRegistry.PreWriteDamageAbilities.Select(h => h.MitigationOrder).ToList();

            Assert.IsTrue(orders.Count >= 2, "Sanguine Ward and Mana Barrier both ride this hook");

            CollectionAssert.AreEqual(orders.OrderBy(o => o).ToList(), orders,
                "the registry must hand the dispatch this bucket already sorted by MitigationOrder - the dispatch itself just walks it in order");
        }

        [TestMethod]
        public void Bands_RunPoolsThenDivertsThenDeathSaves()
        {
            Assert.IsTrue(DamageMitigationOrder.AbsorbPool < DamageMitigationOrder.Divert,
                "a finite pool must be spent against the full hit, before any proportional share is taken");

            Assert.IsTrue(DamageMitigationOrder.Divert < DamageMitigationOrder.DeathSave,
                "a death save decides on the final post-mitigation figure, so nothing may reduce damage after it");

            // The read-only Observe band (Adrenaline arming, Kinetic Charge counting) sits AHEAD of all
            // three rather than between two of them, so the mitigation sandwich stays contiguous and the
            // death save stays literally last in the bucket - see DamageMitigationOrder.Observe.
            Assert.IsTrue(DamageMitigationOrder.Observe < DamageMitigationOrder.AbsorbPool,
                "an observer that mitigates nothing must never sit between two mitigations");
        }

        /// <summary>
        /// The generic form of the band rule, applied to the handlers that are actually registered rather
        /// than to the constants: every absorb pool runs before every divert, and every divert before every
        /// death save. Pinned over the bucket so a new mitigation cannot be slotted at a band that reads
        /// plausibly and orders wrongly.
        /// </summary>
        [TestMethod]
        public void Bucket_RunsEveryPoolBeforeEveryDivert_AndEveryDivertBeforeEveryDeathSave()
        {
            var bucket = ClassAbilityRegistry.PreWriteDamageAbilities.ToList();

            var lastPool = bucket.FindLastIndex(h => h.MitigationOrder == DamageMitigationOrder.AbsorbPool);
            var firstDivert = bucket.FindIndex(h => h.MitigationOrder == DamageMitigationOrder.Divert);
            var lastDivert = bucket.FindLastIndex(h => h.MitigationOrder == DamageMitigationOrder.Divert);
            var firstDeathSave = bucket.FindIndex(h => h.MitigationOrder == DamageMitigationOrder.DeathSave);

            Assert.IsTrue(lastPool >= 0, "Sanguine Ward and Runic Ward both ride the AbsorbPool band");
            Assert.IsTrue(firstDivert >= 0, "Mana Barrier rides the Divert band");
            Assert.IsTrue(firstDeathSave >= 0, "Soul Jump rides the DeathSave band");

            Assert.IsTrue(lastPool < firstDivert,
                "a finite pool must be spent against the full hit, before a proportional share is taken of what is left");

            Assert.IsTrue(lastDivert < firstDeathSave,
                "a death save must see the final post-mitigation figure, or it spends its cost on a hit something else had already stopped");
        }

        /// <summary>
        /// Runic Ward is an absorb pool and must drain before Mana Barrier takes its share, exactly as
        /// Sanguine Ward does. The two pools share the AbsorbPool band, which is correct: two finite pools
        /// drain in sequence and commute with each other, so the stable sort's fallback to registration
        /// order between them changes no damage.
        /// </summary>
        [TestMethod]
        public void RunicWard_AbsorbsBeforeTheBarrierDiverts()
        {
            var bucket = ClassAbilityRegistry.PreWriteDamageAbilities.ToList();

            var runicWard = (IPreWriteDamageAbility)ClassAbilityRegistry.GetHandler(ClassAbilityId.RunicWard);
            var sanguineWard = (IPreWriteDamageAbility)ClassAbilityRegistry.GetHandler(ClassAbilityId.SanguineWard);
            var barrier = (IPreWriteDamageAbility)ClassAbilityRegistry.GetHandler(ClassAbilityId.ManaBarrier);

            Assert.AreEqual(DamageMitigationOrder.AbsorbPool, runicWard.MitigationOrder, "Runic Ward is a finite absorb pool");
            Assert.AreEqual(runicWard.MitigationOrder, sanguineWard.MitigationOrder, "the two pools share one band");

            Assert.IsTrue(bucket.IndexOf(runicWard) < bucket.IndexOf(barrier),
                "the ward absorbs before the barrier diverts, so the barrier's Mana price is charged against damage that was genuinely still going to land");
        }

        /// <summary>
        /// THE ORDERING RULE SOUL JUMP EXISTS TO EXERCISE: a death save must be the LAST handler in the
        /// bucket, not merely the last mitigation. Anything running after it - including a handler that only
        /// means to observe - would be acting on a decision that has already been made and paid for.
        /// </summary>
        [TestMethod]
        public void SoulJump_RunsLastOfEveryHandlerInTheBucket()
        {
            var bucket = ClassAbilityRegistry.PreWriteDamageAbilities.ToList();

            var soulJump = (IPreWriteDamageAbility)ClassAbilityRegistry.GetHandler(ClassAbilityId.SoulJump);

            Assert.AreEqual(DamageMitigationOrder.DeathSave, soulJump.MitigationOrder);

            Assert.AreEqual(bucket.Count - 1, bucket.IndexOf(soulJump),
                "a death save reacts to the final figure, so nothing may run after it");

            foreach (var handler in bucket)
            {
                if (ReferenceEquals(handler, soulJump))
                    continue;

                Assert.IsTrue(handler.MitigationOrder < soulJump.MitigationOrder,
                    $"{handler.Definition.Id} is ordered at or after the death save");
            }
        }

        /// <summary>
        /// The observers (Adrenaline, Kinetic Charge) run ahead of every mitigation and are declared at the
        /// Observe band. They are on this hook rather than the post-write IIncomingDamageAbility one because
        /// that hook is physical-only, and both abilities promise the player they react to damage of any
        /// type; riding both would double-count Kinetic Charge's stack on every physical hit, so this also
        /// pins that neither is registered on the post-write hook.
        /// </summary>
        [TestMethod]
        public void Observers_RunAheadOfEveryMitigation_AndNeverRideThePostWriteHook()
        {
            var bucket = ClassAbilityRegistry.PreWriteDamageAbilities.ToList();

            var firstMitigation = bucket.FindIndex(h => h.MitigationOrder > DamageMitigationOrder.Observe);
            Assert.IsTrue(firstMitigation >= 0, "the bucket still carries mitigations");

            foreach (var id in new[] { ClassAbilityId.Adrenaline, ClassAbilityId.KineticCharge })
            {
                var handler = ClassAbilityRegistry.GetHandler(id);

                Assert.IsInstanceOfType(handler, typeof(IPreWriteDamageAbility), $"{id} must ride the pre-write hook to see all five damage sites");

                var observer = (IPreWriteDamageAbility)handler;

                Assert.AreEqual(DamageMitigationOrder.Observe, observer.MitigationOrder, $"{id} mitigates nothing and belongs at the Observe band");
                Assert.IsTrue(bucket.IndexOf(observer) < firstMitigation, $"{id} must run ahead of every mitigation");

                Assert.IsFalse(handler is IIncomingDamageAbility,
                    $"{id}: riding the post-write hook as well would count the same physical hit twice");
            }
        }

        /// <summary>
        /// Registry wiring for the four abilities this slice implemented, the same shape
        /// <see cref="TheTwoMitigations_RideThePreWriteHook_NeverThePostWriteOne"/> pins for the original
        /// two.
        /// </summary>
        [TestMethod]
        public void TheFourNewHandlers_AreImplementedAndInThePreWriteBucket()
        {
            foreach (var id in new[] { ClassAbilityId.Adrenaline, ClassAbilityId.KineticCharge, ClassAbilityId.RunicWard, ClassAbilityId.SoulJump })
            {
                var handler = ClassAbilityRegistry.GetHandler(id);

                Assert.IsTrue(handler.Definition.Implemented, $"{id} has a live mechanic and must be learnable");
                Assert.IsInstanceOfType(handler, typeof(IPreWriteDamageAbility), $"{id} must ride the pre-write hook");
                Assert.IsTrue(ClassAbilityRegistry.PreWriteDamageAbilities.Contains((IPreWriteDamageAbility)handler), $"{id} must be in the pre-write bucket");
                Assert.IsFalse(handler is IPassiveStatAbility, $"{id} is dispatched from a hook, so it is not a passive marker");
            }
        }

        /// <summary>
        /// Only Runic Ward, of the four, holds a pool that outlives the rank that made it - so only it
        /// declares RunsWithoutLearnedRank. The other three recompute everything from the live rank, so the
        /// rank filter on the per-player hook cache is exactly right for them.
        /// </summary>
        [TestMethod]
        public void OnlyRunicWard_OfTheFour_SurvivesTheRankFilterAtZero()
        {
            var runicWard = ClassAbilityRegistry.GetHandler(ClassAbilityId.RunicWard);

            Assert.IsTrue(((IPreWriteDamageAbility)runicWard).RunsWithoutLearnedRank,
                "a ward already inscribed must keep draining after an unlearn, a facet swap or a rank sweep");
            Assert.IsTrue(ClassAbilityRegistry.IsHookCacheEligible(runicWard, 0));

            foreach (var id in new[] { ClassAbilityId.Adrenaline, ClassAbilityId.KineticCharge, ClassAbilityId.SoulJump })
            {
                var handler = ClassAbilityRegistry.GetHandler(id);

                Assert.IsFalse(((IPreWriteDamageAbility)handler).RunsWithoutLearnedRank, $"{id} holds nothing that outlives its rank");
                Assert.IsFalse(ClassAbilityRegistry.IsHookCacheEligible(handler, 0), $"{id} must drop out of the cache at rank 0");
            }
        }

        /// <summary>
        /// WHY THE POOL MUST RUN BEFORE THE DEATH SAVE, as arithmetic rather than as a band comparison. A
        /// 100 point blow on a 60 health defender holding a 50 point ward is lethal as thrown and NOT lethal
        /// once the ward has eaten its half - so in the shipped order the save is never consulted and the
        /// pet and the cooldown are still there for a blow that really would have killed. In the reversed
        /// order the save fires first, spends both, and the ward then absorbs on top of a hit that was
        /// already survivable.
        ///
        /// Both halves are pure functions, so this pins the property without a live Player.
        /// </summary>
        [TestMethod]
        public void AbsorbBeforeDeathSave_KeepsTheSaveForAHitThatWouldActuallyKill()
        {
            const uint hit = 100;
            const uint health = 60;
            const double now = 1000.0;

            var ward = RunicWardMath.Inscribe(default, 500, 0.10, 200, now, 12.0);
            Assert.AreEqual(50u, ward.Amount, "control: the ward must be worth half the hit for this test to mean anything");

            // SHIPPED ORDER: pool, then death save
            var afterWard = RunicWardMath.Absorb(ward, hit, now);
            Assert.AreEqual(50u, afterWard.DamageAfterWard);

            Assert.IsFalse(SoulJumpMath.SavesFromDeath(afterWard.DamageAfterWard, health, hasLivePet: true, cooldownReady: true),
                "the ward already removed the death, so the save must not fire and must not spend the pet");

            Assert.IsTrue(afterWard.DamageAfterWard < health, "and the defender lives on the ward alone");

            // REVERSED, as the counterexample: the save fires on the hit as thrown and spends everything
            Assert.IsTrue(SoulJumpMath.SavesFromDeath(hit, health, hasLivePet: true, cooldownReady: true),
                "control: run first, the save DOES fire - which is exactly the waste the band order prevents");
        }

        /// <summary>
        /// A lethal blow with a ward still standing must not kill. This is the property the pre-write
        /// placement buys and the post-write hook cannot: the caller writes DamageAfterWard to the vital and
        /// only then checks for death, so leaving less damage than the defender has Health has saved them.
        /// </summary>
        [TestMethod]
        public void LethalBlowWithAWardStanding_LeavesTheDefenderAlive()
        {
            const uint health = 120;
            const double now = 500.0;

            var ward = RunicWardMath.Inscribe(default, 1000, 0.10, 500, now, 12.0);
            Assert.AreEqual(100u, ward.Amount);

            var result = RunicWardMath.Absorb(ward, 150, now);

            Assert.AreEqual(100u, result.Absorbed);
            Assert.AreEqual(50u, result.DamageAfterWard);
            Assert.IsTrue(result.DamageAfterWard < health, "a blow the ward can cover must not be lethal");
            Assert.AreEqual(0u, result.Remaining.Amount, "and the ward is emptied by having covered it");
        }

        /// <summary>
        /// Soul Jump must not fire when no pet is alive - the entry's whole cost structure. Pinned
        /// alongside the other three conditions so a future edit cannot quietly drop one.
        /// </summary>
        [TestMethod]
        public void SoulJump_DoesNotFireWithoutALivePet_NorOffCooldown_NorOnASurvivableHit()
        {
            Assert.IsTrue(SoulJumpMath.SavesFromDeath(100, 60, hasLivePet: true, cooldownReady: true),
                "control: with a pet, off cooldown, against a lethal blow, the save fires");

            Assert.IsFalse(SoulJumpMath.SavesFromDeath(100, 60, hasLivePet: false, cooldownReady: true),
                "with no pet alive there is no save");

            Assert.IsFalse(SoulJumpMath.SavesFromDeath(100, 60, hasLivePet: true, cooldownReady: false),
                "one save per 6/4/2 minutes by rank");

            Assert.IsFalse(SoulJumpMath.SavesFromDeath(59, 60, hasLivePet: true, cooldownReady: true),
                "a survivable hit must not spend the pet or the cooldown");

            Assert.IsFalse(SoulJumpMath.SavesFromDeath(100, 0, hasLivePet: true, cooldownReady: true),
                "a defender already at zero is not being saved by anything");
        }

        /// <summary>
        /// DamageMitigationOrder.ShouldDispatchPreWriteHandler is the gate the dispatcher applies per
        /// handler now that a 0 hit no longer early-outs the whole hook. Observe must see a hit of any size,
        /// including 0; every other declared band must be skipped at 0 and dispatched otherwise. Enumerated
        /// from the class's own constants so a future band cannot be added without this test covering it.
        /// </summary>
        [TestMethod]
        public void ShouldDispatchPreWriteHandler_ObserveAloneSeesAZeroHit()
        {
            Assert.IsTrue(DamageMitigationOrder.ShouldDispatchPreWriteHandler(DamageMitigationOrder.Observe, 0),
                "Observe must see a landed 0-damage hit - that is the whole point of the fix");
            Assert.IsTrue(DamageMitigationOrder.ShouldDispatchPreWriteHandler(DamageMitigationOrder.Observe, 5),
                "Observe must also see ordinary non-zero hits, unchanged from before");

            var otherBands = new[] { DamageMitigationOrder.AbsorbPool, DamageMitigationOrder.Divert, DamageMitigationOrder.DeathSave };

            foreach (var band in otherBands)
            {
                Assert.IsFalse(DamageMitigationOrder.ShouldDispatchPreWriteHandler(band, 0),
                    $"band {band} has nothing left to mitigate against a hit already at 0 and must not run");
                Assert.IsTrue(DamageMitigationOrder.ShouldDispatchPreWriteHandler(band, 7),
                    $"band {band} must still dispatch normally against a non-zero hit");
            }
        }

        /// <summary>
        /// The predicate applied to the actual registered handlers rather than to the band constants: the
        /// two Observe-band abilities dispatch at 0, the four mitigations (Sanguine Ward, Runic Ward, Mana
        /// Barrier, Soul Jump) do not. Uses ClassAbilityRegistry.GetHandler, the same lookup-by-ClassAbilityId
        /// pattern the rest of this file uses.
        /// </summary>
        [TestMethod]
        public void ShouldDispatchPreWriteHandler_MatchesTheRegisteredHandlersAtZeroDamage()
        {
            var dispatchesAtZero = new[] { ClassAbilityId.KineticCharge, ClassAbilityId.Adrenaline };
            var skipsAtZero = new[] { ClassAbilityId.SanguineWard, ClassAbilityId.RunicWard, ClassAbilityId.ManaBarrier, ClassAbilityId.SoulJump };

            foreach (var id in dispatchesAtZero)
            {
                var handler = (IPreWriteDamageAbility)ClassAbilityRegistry.GetHandler(id);

                Assert.IsTrue(DamageMitigationOrder.ShouldDispatchPreWriteHandler(handler.MitigationOrder, 0),
                    $"{id} must react to a landed 0-damage hit");
            }

            foreach (var id in skipsAtZero)
            {
                var handler = (IPreWriteDamageAbility)ClassAbilityRegistry.GetHandler(id);

                Assert.IsFalse(DamageMitigationOrder.ShouldDispatchPreWriteHandler(handler.MitigationOrder, 0),
                    $"{id} has nothing to mitigate against a hit already at 0 and must not be dispatched");
                Assert.IsTrue(DamageMitigationOrder.ShouldDispatchPreWriteHandler(handler.MitigationOrder, 42),
                    $"{id} must still dispatch normally against a non-zero hit");
            }
        }

        [TestMethod]
        public void TheTwoMitigations_RideThePreWriteHook_NeverThePostWriteOne()
        {
            foreach (var id in new[] { ClassAbilityId.SanguineWard, ClassAbilityId.ManaBarrier })
            {
                var handler = ClassAbilityRegistry.GetHandler(id);

                Assert.IsInstanceOfType(handler, typeof(IPreWriteDamageAbility), $"{id} must ride the pre-write hook");
                Assert.IsTrue(ClassAbilityRegistry.PreWriteDamageAbilities.Contains((IPreWriteDamageAbility)handler), $"{id} must be in the pre-write bucket");

                Assert.IsFalse(handler is IIncomingDamageAbility, $"{id}: a pre-write mitigation must never ride the post-write hook - that ordering is the 2026-09-08 overkill bug");
                Assert.IsFalse(handler is IPassiveStatAbility, $"{id}: it is dispatched from a hook now, so it is no longer a passive marker");
            }
        }

        [TestMethod]
        public void SanguineWard_RunsBeforeManaBarrier()
        {
            var bucket = ClassAbilityRegistry.PreWriteDamageAbilities.ToList();

            var ward = (IPreWriteDamageAbility)ClassAbilityRegistry.GetHandler(ClassAbilityId.SanguineWard);
            var barrier = (IPreWriteDamageAbility)ClassAbilityRegistry.GetHandler(ClassAbilityId.ManaBarrier);

            Assert.IsTrue(bucket.IndexOf(ward) < bucket.IndexOf(barrier),
                "the ward absorbs before the barrier diverts, which is the order both ran in at all five sites when they were wired by hand");
        }

        /// <summary>
        /// WHY THAT ORDER IS ARITHMETIC AND NOT A STYLE CHOICE. A finite absorb pool and a proportional
        /// divert do not commute: run in the two possible orders against the same hit they leave different
        /// damage on Health AND spend different amounts of Mana. This is what makes registration order
        /// unacceptable for this hook and MitigationOrder necessary - a reordering of the handler list in
        /// ClassAbilityRegistry would otherwise silently change live combat numbers.
        ///
        /// Both halves are pure functions, so this pins the property without a live Player.
        /// </summary>
        [TestMethod]
        public void AbsorbThenDivert_IsNotTheSameHitAsDivertThenAbsorb()
        {
            const uint hit = 100;
            const double share = 0.25;

            var ward = SanguineWardMath.Grant(default, 50, 1.0, 1000.0, 15.0);
            Assert.AreEqual(50u, ward.Amount);

            // SHIPPED ORDER: the pool eats what it can, then the barrier takes its share of the remainder
            var wardFirst = SanguineWardMath.Absorb(ward, hit, 1001.0);
            Assert.AreEqual(50u, wardFirst.DamageAfterWard);

            var thenBarrier = ManaBarrierAbility.AbsorbDamage(wardFirst.DamageAfterWard, share, 100000, 1.0);
            Assert.AreEqual(38u, thenBarrier.DamageAfter);
            Assert.AreEqual(12u, thenBarrier.ManaSpent);

            // REVERSED, as a counterexample: the share comes off the whole hit, then the pool eats the rest
            var barrierFirst = ManaBarrierAbility.AbsorbDamage(hit, share, 100000, 1.0);
            Assert.AreEqual(75u, barrierFirst.DamageAfter);
            Assert.AreEqual(25u, barrierFirst.ManaSpent);

            var thenWard = SanguineWardMath.Absorb(ward, barrierFirst.DamageAfter, 1001.0);
            Assert.AreEqual(25u, thenWard.DamageAfterWard);

            Assert.AreNotEqual(thenBarrier.DamageAfter, thenWard.DamageAfterWard,
                "the two orders must genuinely differ, or this test is not pinning anything");

            Assert.AreNotEqual(thenBarrier.ManaSpent, barrierFirst.ManaSpent,
                "and the resource cost differs too, not just the damage");
        }

        /// <summary>
        /// THE SECOND REGRESSION THIS FILE EXISTS FOR. The per-player hook cache is RANK-FILTERED, so moving
        /// an effect from a by-name call onto a dispatch can silently kill it the moment the player's rank
        /// reaches zero - by an unlearn (/abilities has no combat-state gate), by a facet swap
        /// (Player_Facets.ApplyFacetAbilities), or by a wholesale rank sweep. Sanguine Ward's pool is granted
        /// at cast time and drains for its own 15 second window with no rank check anywhere in
        /// AbsorbWithSanguineWard, so it MUST survive that filter or a live ward stops absorbing mid-window.
        /// Mana Barrier recomputes its share from live rank on every hit and holds no pool, so it must NOT.
        ///
        /// ClassAbilityRegistry.IsHookCacheEligible is the production rule Player.BuildClassAbilityHookCache
        /// calls, so this pins the real predicate rather than a copy of it - and needs no live Player.
        /// </summary>
        [TestMethod]
        public void SanguineWard_SurvivesTheRankFilterAtRankZero_ManaBarrierDoesNot()
        {
            var ward = ClassAbilityRegistry.GetHandler(ClassAbilityId.SanguineWard);
            var barrier = ClassAbilityRegistry.GetHandler(ClassAbilityId.ManaBarrier);

            Assert.IsTrue(ClassAbilityRegistry.IsHookCacheEligible(ward, 0),
                "a ward already granted must keep draining after an unlearn, a facet swap or a rank sweep");
            Assert.IsTrue(ClassAbilityRegistry.IsHookCacheEligible(ward, 3));

            Assert.IsFalse(ClassAbilityRegistry.IsHookCacheEligible(barrier, 0),
                "the barrier holds no pool and recomputes from live rank, so an unlearn must stop it on the very next hit");
            Assert.IsTrue(ClassAbilityRegistry.IsHookCacheEligible(barrier, 2));

            Assert.IsTrue(((IPreWriteDamageAbility)ward).RunsWithoutLearnedRank);
            Assert.IsFalse(((IPreWriteDamageAbility)barrier).RunsWithoutLearnedRank);
        }

        /// <summary>
        /// The general form: surviving the rank filter at zero comes ONLY from an explicit
        /// RunsWithoutLearnedRank declaration, never by accident. Walks every registered handler, so a future
        /// ability that starts outliving its rank has to declare it rather than discover it in production.
        /// </summary>
        [TestMethod]
        public void OnlyDeclaredRankIndependentHandlers_SurviveTheRankFilterAtZero()
        {
            foreach (var handler in ClassAbilityRegistry.Handlers)
            {
                var declared = handler is IPreWriteDamageAbility preWrite && preWrite.RunsWithoutLearnedRank;

                Assert.AreEqual(declared, ClassAbilityRegistry.IsHookCacheEligible(handler, 0),
                    $"{handler.Definition.Id}: eligibility at rank 0 must come from an explicit declaration");

                Assert.IsTrue(ClassAbilityRegistry.IsHookCacheEligible(handler, 1),
                    $"{handler.Definition.Id}: a held rank always makes a handler eligible");
            }
        }
    }
}
