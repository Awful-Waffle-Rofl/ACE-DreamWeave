using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.ClassAbilities.Abilities;
using ACE.Server.Entity;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Summon 2x pet-pair expiry sync (PetLifespanSync) and Empowered Summons' Loyalty duration rider,
    /// both wired from CombatPet.Init.
    ///
    /// SCOPE NOTE: the same limit as SummonFollowAssistTests - no test in this project can construct a live
    /// Player, and a CombatPet needs a live owner, a landblock and a physics object. So these cover the pure
    /// pieces: which slot holds the sibling, whether that sibling's clock may be rewritten, whether the
    /// rewrite would shorten it, and the duration multiplier itself. The rewrite (CreationTimestamp +
    /// Lifespan + NextHeartbeatTime) and the Loyalty read need an in-game check - see Docs/VERIFY-QUEUE.md.
    /// </summary>
    [TestClass]
    public class Summon2xLifespanSyncTests
    {
        // reference identity is the whole point of SelectSibling, so the fakes must be distinct instances
        // of a reference type that are never equal by value
        private static readonly object PetA = new object();
        private static readonly object PetB = new object();

        // ---------------------------------------------------------------- SelectSibling

        [TestMethod]
        public void SelectSibling_FirstPetOfAPair_HasNoSibling()
        {
            // Pet.Init has just written the first pet into the primary slot; the secondary is still free
            Assert.IsNull(PetLifespanSync.SelectSibling(PetA, PetA, null));
        }

        [TestMethod]
        public void SelectSibling_SecondPetOfAPair_IsThePrimary()
        {
            Assert.AreSame(PetA, PetLifespanSync.SelectSibling(PetB, PetA, PetB));
        }

        [TestMethod]
        public void SelectSibling_PetToppingUpThePrimarySlot_IsTheSurvivorInTheSecondary()
        {
            // the desync case: the primary pet expired, the secondary survived, and a new pet took the
            // freed primary slot. The survivor is the one that needs re-upping.
            Assert.AreSame(PetB, PetLifespanSync.SelectSibling(PetA, PetA, PetB));
        }

        [TestMethod]
        public void SelectSibling_NoPetSummoned_SelectsNothing()
        {
            Assert.IsNull(PetLifespanSync.SelectSibling<object>(null, PetA, PetB));
        }

        [TestMethod]
        public void SelectSibling_NewPetInBothSlots_SelectsNothing()
        {
            // cannot happen today, but selecting the new pet as its own sibling would make the caller
            // overwrite the clock it just read
            Assert.IsNull(PetLifespanSync.SelectSibling(PetA, PetA, PetA));
        }

        // ---------------------------------------------------------------- ShouldSync

        [TestMethod]
        public void ShouldSync_TwoLiveCombatPets_Syncs()
        {
            Assert.IsTrue(PetLifespanSync.ShouldSync(justSummonedIsCombatPet: true, siblingExists: true,
                siblingIsCombatPet: true, siblingDestroyed: false));
        }

        [TestMethod]
        public void ShouldSync_NoSibling_DoesNotSync()
        {
            Assert.IsFalse(PetLifespanSync.ShouldSync(justSummonedIsCombatPet: true, siblingExists: false,
                siblingIsCombatPet: false, siblingDestroyed: false));
        }

        [TestMethod]
        public void ShouldSync_PassiveSummon_DoesNotSync()
        {
            // a passive pet device stows rather than pairs, and passive pets carry no lifespan to align
            Assert.IsFalse(PetLifespanSync.ShouldSync(justSummonedIsCombatPet: false, siblingExists: true,
                siblingIsCombatPet: true, siblingDestroyed: false));
        }

        [TestMethod]
        public void ShouldSync_PassiveSibling_DoesNotSync()
        {
            Assert.IsFalse(PetLifespanSync.ShouldSync(justSummonedIsCombatPet: true, siblingExists: true,
                siblingIsCombatPet: false, siblingDestroyed: false));
        }

        [TestMethod]
        public void ShouldSync_DestroyedSibling_DoesNotSync()
        {
            // its slot is about to be cleared by WorldObject.Destroy; extending a corpse's clock is noise
            Assert.IsFalse(PetLifespanSync.ShouldSync(justSummonedIsCombatPet: true, siblingExists: true,
                siblingIsCombatPet: true, siblingDestroyed: true));
        }

        // ---------------------------------------------------------------- ExtendsSibling

        // both pets of one activation: same second, same lifespan
        private const int T = 1_700_000_000;
        private const int BaseLifespan = 43;

        [TestMethod]
        public void ExtendsSibling_SamePair_IsAllowedAsANoOp()
        {
            Assert.IsTrue(PetLifespanSync.ExtendsSibling(T, BaseLifespan, T, BaseLifespan));
        }

        [TestMethod]
        public void ExtendsSibling_NewerPetSameLifespan_Extends()
        {
            // the survivor top-up case: a charge spent 45 s later re-ups the pet still out
            Assert.IsTrue(PetLifespanSync.ExtendsSibling(T + 45, BaseLifespan, T, BaseLifespan));
        }

        [TestMethod]
        public void ExtendsSibling_NewerButShorterLifespan_StillExtends_WhenTheGapCoversIt()
        {
            // Loyalty buff lapsed (60 s pet -> 43 s pet), but 45 s of the survivor's life is already spent
            Assert.IsTrue(PetLifespanSync.ExtendsSibling(T + 45, BaseLifespan, T, 60));
        }

        [TestMethod]
        public void ExtendsSibling_NewerButShorterLifespan_DoesNotExtend_WhenSummonedRightAfter()
        {
            // the case that actually reaches this guard: the other pet of a 60 s pair was slain one second
            // in and replaced through Soul Tether's free resummon, with Loyalty no longer buffed. Adopting
            // the new 43 s clock would cut the survivor from T+60 to T+44.
            Assert.IsFalse(PetLifespanSync.ExtendsSibling(T + 1, BaseLifespan, T, 60));
        }

        [TestMethod]
        public void ExtendsSibling_NearIntMaxDeadline_DoesNotOverflow()
        {
            // int.MaxValue + a lifespan must not wrap negative and invert the comparison
            Assert.IsTrue(PetLifespanSync.ExtendsSibling(int.MaxValue, 60, int.MaxValue, BaseLifespan));
            Assert.IsFalse(PetLifespanSync.ExtendsSibling(int.MaxValue, BaseLifespan, int.MaxValue, 60));
        }

        // ---------------------------------------------------------------- duration rider

        [TestMethod]
        public void DurationMultiplier_Unlearned_IsNeutralHoweverMuchLoyalty()
        {
            // the rank gate is what makes the ABILITY the purchase rather than the skill
            Assert.AreEqual(1.0f, EmpoweredSummonsAbility.DurationMultiplier(0, 0.40, 1.00), 0.0001f);
        }

        [TestMethod]
        public void DurationMultiplier_LoyaltyRider_Applies()
        {
            // 400 effective Loyalty at the shipped 10 points per +1% Trained = +40%
            Assert.AreEqual(1.40f, EmpoweredSummonsAbility.DurationMultiplier(1, 0.40, 1.00), 0.0001f);
        }

        [TestMethod]
        public void DurationMultiplier_IsNotRankScaled()
        {
            // ranks buy stats, Loyalty buys duration - rank 1 and rank 3 must read the same here
            Assert.AreEqual(EmpoweredSummonsAbility.DurationMultiplier(1, 0.40, 1.00),
                EmpoweredSummonsAbility.DurationMultiplier(3, 0.40, 1.00), 0.0001f);
        }

        [TestMethod]
        public void DurationMultiplier_ClampsToTheCap()
        {
            Assert.AreEqual(2.0f, EmpoweredSummonsAbility.DurationMultiplier(3, 5.00, 1.00), 0.0001f);
        }

        [TestMethod]
        public void DurationMultiplier_ZeroCap_IsNeutral()
        {
            // a server that tunes the cap to 0 turns the rider off without unlearning anything
            Assert.AreEqual(1.0f, EmpoweredSummonsAbility.DurationMultiplier(3, 0.40, 0.0), 0.0001f);
        }

        [TestMethod]
        public void DurationMultiplier_NegativeInputs_AreNeutralRatherThanShortening()
        {
            Assert.AreEqual(1.0f, EmpoweredSummonsAbility.DurationMultiplier(3, -0.40, 1.00), 0.0001f);
            Assert.AreEqual(1.0f, EmpoweredSummonsAbility.DurationMultiplier(3, 0.40, -1.00), 0.0001f);
        }
    }
}
