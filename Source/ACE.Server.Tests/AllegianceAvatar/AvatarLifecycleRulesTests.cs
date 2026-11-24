using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Avatars;

namespace ACE.Server.Tests.Avatars
{
    /// <summary>Every row of the spec's action table, through the pure classifiers.</summary>
    [TestClass]
    public class AvatarLifecycleRulesTests
    {
        private static readonly AvatarTuning T = AvatarTuning.Defaults; // min_active_accounts 2

        private static AvatarState Bound(uint id) => new AvatarState { AvatarId = id, MonarchGuid = 1000 + id, State = AvatarBindState.Bound };

        // ---- break ---------------------------------------------------------------------------------------------

        [TestMethod]
        public void BranchBreak_MonarchActive_InfoStays()
        {
            Assert.AreEqual(BreakAvatarEffect.InfoStays,
                AvatarLifecycleRules.ClassifyBreak(actorIsActingKeeper: false, monarchIdle: false, actorIsMonarch: false, activeAccountsAfter: 5, T));
        }

        [TestMethod]
        public void BranchBreak_MonarchActive_LeavingTooFewActive_StillInfoStays()
        {
            // A vassal leaving never asks about the Avatar: only the monarch's own break is theirs to confirm.
            Assert.AreEqual(BreakAvatarEffect.InfoStays,
                AvatarLifecycleRules.ClassifyBreak(actorIsActingKeeper: false, monarchIdle: false, actorIsMonarch: false, activeAccountsAfter: 1, T));
        }

        [TestMethod]
        public void KeeperBreak_MonarchIdle_Inheritance()
        {
            Assert.AreEqual(BreakAvatarEffect.Inheritance,
                AvatarLifecycleRules.ClassifyBreak(actorIsActingKeeper: true, monarchIdle: true, actorIsMonarch: false, activeAccountsAfter: 5, T));
        }

        [TestMethod]
        public void NonKeeperBreak_MonarchIdle_InfoStays()
        {
            Assert.AreEqual(BreakAvatarEffect.InfoStays,
                AvatarLifecycleRules.ClassifyBreak(actorIsActingKeeper: false, monarchIdle: true, actorIsMonarch: false, activeAccountsAfter: 5, T));
        }

        [TestMethod]
        public void MonarchBootsLastVassal_GoesInactive()
        {
            Assert.AreEqual(BreakAvatarEffect.GoesInactive,
                AvatarLifecycleRules.ClassifyBreak(actorIsActingKeeper: false, monarchIdle: false, actorIsMonarch: true, activeAccountsAfter: 1, T));
        }

        [TestMethod]
        public void MonarchBreak_EnoughActiveLeft_InfoStays()
        {
            Assert.AreEqual(BreakAvatarEffect.InfoStays,
                AvatarLifecycleRules.ClassifyBreak(actorIsActingKeeper: false, monarchIdle: false, actorIsMonarch: true, activeAccountsAfter: 2, T),
                "exactly min_active_accounts left keeps the Avatar active");
        }

        [TestMethod]
        public void KeeperBreaksLeavingTooFewActive_InheritanceWins()
        {
            // Inheritance needs !actorIsMonarch and GoesInactive needs actorIsMonarch, so the two are mutually exclusive by
            // construction and the classifier's ordering cannot change this result. The row documents the ruling
            // (pre-flight 22); MonarchFlagged_NeverInherits pins the monarch side of the exclusion.
            Assert.AreEqual(BreakAvatarEffect.Inheritance,
                AvatarLifecycleRules.ClassifyBreak(actorIsActingKeeper: true, monarchIdle: true, actorIsMonarch: false, activeAccountsAfter: 0, T));
        }

        [TestMethod]
        public void MonarchFlagged_NeverInherits()
        {
            // Every Inheritance input set, but the actor is the monarch: a monarch cannot inherit their own Avatar, and
            // leaving no active accounts behind is GoesInactive.
            Assert.AreEqual(BreakAvatarEffect.GoesInactive,
                AvatarLifecycleRules.ClassifyBreak(actorIsActingKeeper: true, monarchIdle: true, actorIsMonarch: true, activeAccountsAfter: 0, T));
        }

        [TestMethod]
        public void ActingKeeperFlag_WithMonarchActive_IsNotInheritance()
        {
            // A stale Keeper flag never hands the Avatar to a branch while the monarch is active.
            Assert.AreEqual(BreakAvatarEffect.InfoStays,
                AvatarLifecycleRules.ClassifyBreak(actorIsActingKeeper: true, monarchIdle: false, actorIsMonarch: false, activeAccountsAfter: 5, T));
        }

        // ---- swear ---------------------------------------------------------------------------------------------

        [TestMethod]
        public void MonarchSwearsToAvatarHolder_Absorb()
        {
            Assert.AreEqual(SwearAvatarEffect.Absorb, AvatarLifecycleRules.ClassifySwear(true, Bound(1), Bound(2)));
        }

        [TestMethod]
        public void MonarchSwearsToNoAvatar_Adopt()
        {
            Assert.AreEqual(SwearAvatarEffect.Adopt, AvatarLifecycleRules.ClassifySwear(true, Bound(1), null));
        }

        [TestMethod]
        public void MonarchSwearsToAllegianceWithUnboundAvatar_Adopt()
        {
            var absorbed = Bound(2);
            absorbed.State = AvatarBindState.Absorbed;
            Assert.AreEqual(SwearAvatarEffect.Adopt, AvatarLifecycleRules.ClassifySwear(true, Bound(1), absorbed));
        }

        [TestMethod]
        public void VassalSwear_None()
        {
            Assert.AreEqual(SwearAvatarEffect.None, AvatarLifecycleRules.ClassifySwear(false, Bound(1), Bound(2)));
        }

        [TestMethod]
        public void MonarchWithoutAvatarSwears_None()
        {
            Assert.AreEqual(SwearAvatarEffect.None, AvatarLifecycleRules.ClassifySwear(true, null, Bound(2)));

            var retired = Bound(1);
            retired.State = AvatarBindState.Retired;
            Assert.AreEqual(SwearAvatarEffect.None, AvatarLifecycleRules.ClassifySwear(true, retired, Bound(2)));
        }
    }
}
