using System;
using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Entity.AccountVault;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The pure half of the mule panel's deferred refresh (MuleWithdrawRefresh.cs). Every test here
    /// reaches ONLY the pure functions/types - MuleWithdrawRefresh.Decide, MuleWithdrawRefresh.ActionsAfterWithdraw
    /// and MuleRefreshDebouncer - never PersonalVendor or a live TryWithdrawTransaction. The vendor-side
    /// wiring (including the elapsed-window upgrade in RefreshAfterWithdraw) is covered in
    /// PersonalVendorTests' "Deferred withdraw refresh" region; there is no timer or fire-time gate
    /// anymore - see MuleWithdrawRefresh.cs's class remarks for why a server-initiated send is unsafe here.
    /// </summary>
    [TestClass]
    public class MuleWithdrawRefreshTests
    {
        private static MuleWithdrawOutcome Partial(MuleWithdrawRowKind kind) => new MuleWithdrawOutcome(kind, false);

        private static MuleWithdrawOutcome Emptied(MuleWithdrawRowKind kind) => new MuleWithdrawOutcome(kind, true);

        // ---- Decide (pure function) ----

        [TestMethod]
        public void Decide_OnlyPartialClassGroupLedgerDecrements_Defers()
        {
            var outcomes = new List<MuleWithdrawOutcome>
            {
                Partial(MuleWithdrawRowKind.Class),
                Partial(MuleWithdrawRowKind.Group),
                Partial(MuleWithdrawRowKind.Ledger),
            };

            Assert.AreEqual(MuleRefreshTiming.Defer, MuleWithdrawRefresh.Decide(outcomes, anyReturnedToVault: false));
        }

        [TestMethod]
        public void Decide_EachDeferrableKindAlone_Defers()
        {
            foreach (var kind in new[] { MuleWithdrawRowKind.Class, MuleWithdrawRowKind.Group, MuleWithdrawRowKind.Ledger })
                Assert.AreEqual(MuleRefreshTiming.Defer, MuleWithdrawRefresh.Decide(new[] { Partial(kind) }, false), kind.ToString());
        }

        [TestMethod]
        public void Decide_AnEmptiedClassOrGroupOrLedgerRow_IsImmediate_EvenBesidePartials()
        {
            foreach (var kind in new[] { MuleWithdrawRowKind.Class, MuleWithdrawRowKind.Group, MuleWithdrawRowKind.Ledger })
            {
                var outcomes = new[] { Partial(MuleWithdrawRowKind.Class), Emptied(kind), Partial(MuleWithdrawRowKind.Group) };

                Assert.AreEqual(MuleRefreshTiming.Immediate, MuleWithdrawRefresh.Decide(outcomes, false), $"emptied {kind} must refresh at once");
            }
        }

        [TestMethod]
        public void Decide_AnIndividualItem_IsImmediate_EvenWhenNotFlaggedEmptied()
        {
            // The kind alone decides it: an individual stored item's row always disappears.
            var outcomes = new[] { Partial(MuleWithdrawRowKind.Group), Partial(MuleWithdrawRowKind.Individual) };

            Assert.AreEqual(MuleRefreshTiming.Immediate, MuleWithdrawRefresh.Decide(outcomes, false));
        }

        [TestMethod]
        public void Decide_AnythingReturnedToTheVault_IsImmediate()
        {
            Assert.AreEqual(MuleRefreshTiming.Immediate, MuleWithdrawRefresh.Decide(new[] { Partial(MuleWithdrawRowKind.Class) }, anyReturnedToVault: true));
        }

        [TestMethod]
        public void Decide_NoOutcomes_FailsTowardImmediate()
        {
            Assert.AreEqual(MuleRefreshTiming.Immediate, MuleWithdrawRefresh.Decide(new MuleWithdrawOutcome[0], false));
            Assert.AreEqual(MuleRefreshTiming.Immediate, MuleWithdrawRefresh.Decide(null, false));
        }

        // ---- ActionsAfterWithdraw (owner ruling: one Buy line per withdrawal) ----
        //
        // Reaches the pure plan that PersonalVendor.RefreshAfterWithdraw consumes, including the plan
        // for a timing already upgraded by ApplyElapsedUpgrade; RefreshAfterWithdraw itself needs a
        // live Player and is not reachable here. Emotes are counted the way the live code produces
        // them: Vendor.ApproachVendor plays one vendor emote for any type other than Undef
        // (Vendor.cs:326), plus one for PlayBuyEmoteNow.

        private static int EmotesFor(MuleRefreshActions actions)
        {
            var count = actions.PlayBuyEmoteNow ? 1 : 0;

            if (actions.SendListNow && actions.ListVendorType != ACE.Entity.Enum.VendorType.Undef)
                count++;

            return count;
        }

        [TestMethod]
        public void Actions_DeferredWithdraw_PlaysExactlyOneBuyLine_AndSendsNoList()
        {
            var actions = MuleWithdrawRefresh.ActionsAfterWithdraw(MuleRefreshTiming.Defer);

            Assert.IsFalse(actions.SendListNow, "a deferred withdrawal must not send the list");
            Assert.IsTrue(actions.PlayBuyEmoteNow, "but must play the Buy line now");
            Assert.IsTrue(actions.OpenDeferralWindow);
            Assert.AreEqual(1, EmotesFor(actions), "exactly one line per deferred withdrawal");
        }

        [TestMethod]
        public void Actions_ImmediateWithdraw_PlaysExactlyOneBuyLine_ThroughTheListSend()
        {
            var actions = MuleWithdrawRefresh.ActionsAfterWithdraw(MuleRefreshTiming.Immediate);

            Assert.IsTrue(actions.SendListNow);
            Assert.AreEqual(ACE.Entity.Enum.VendorType.Buy, actions.ListVendorType, "unchanged: the immediate refresh plays the Buy line itself");
            Assert.IsFalse(actions.PlayBuyEmoteNow, "and no second line on top of it");
            Assert.IsFalse(actions.OpenDeferralWindow);
            Assert.AreEqual(1, EmotesFor(actions));
        }

        [TestMethod]
        public void ABurstOfDeferredWithdrawalsThenTheUpgradeClick_PlaysExactlyOneLinePerWithdrawal()
        {
            // Five partial withdrawals inside one window (each plays its own Buy line and defers),
            // then a sixth click after the window has elapsed - which PersonalVendor.RefreshAfterWithdraw
            // upgrades to Immediate (see PersonalVendorTests' ElapsedUpgrade_* tests), so it plays its
            // line through the ordinary immediate-refresh list send. No timer ever fires a line on its own.
            var lines = 0;

            for (var i = 0; i < 5; i++)
                lines += EmotesFor(MuleWithdrawRefresh.ActionsAfterWithdraw(MuleRefreshTiming.Defer));

            lines += EmotesFor(MuleWithdrawRefresh.ActionsAfterWithdraw(MuleRefreshTiming.Immediate));

            Assert.AreEqual(6, lines, "one Buy line per withdrawal, including the elapsed-upgrade click");
        }

        // ---- MuleRefreshDebouncer (fixed window recorded by open time, cancel, per viewer, no timer) ----

        private static readonly DateTime Epoch = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        [TestMethod]
        public void Debouncer_SecondDeferralInsideTheWindow_DoesNotReopenOrPushBack()
        {
            var debouncer = new MuleRefreshDebouncer();

            Assert.IsTrue(debouncer.TryOpenWindow(0x50000001, Epoch), "the first deferral opens the window");
            Assert.IsFalse(debouncer.TryOpenWindow(0x50000001, Epoch.AddSeconds(4)), "a deferral inside an open window must not open another");

            // If the second deferral had pushed the open time back to +4s, the window would not have
            // elapsed by +5s (only 1s would have passed since the pushed-back open). It elapses here,
            // which proves the open time is still the FIRST deferral's.
            Assert.IsTrue(debouncer.HasElapsed(0x50000001, Epoch.AddSeconds(5), MuleWithdrawRefresh.DeferWindowSeconds),
                "the window must still be measured from the FIRST deferral, not extended by the second");
        }

        [TestMethod]
        public void Debouncer_HasElapsed_FalseBeforeTheWindow_TrueAtAndAfterIt()
        {
            var debouncer = new MuleRefreshDebouncer();
            debouncer.TryOpenWindow(0x50000001, Epoch);

            Assert.IsFalse(debouncer.HasElapsed(0x50000001, Epoch.AddSeconds(4), MuleWithdrawRefresh.DeferWindowSeconds), "4s < 5s window: not yet elapsed");
            Assert.IsTrue(debouncer.HasElapsed(0x50000001, Epoch.AddSeconds(5), MuleWithdrawRefresh.DeferWindowSeconds), "exactly 5s: elapsed");
            Assert.IsTrue(debouncer.HasElapsed(0x50000001, Epoch.AddSeconds(6), MuleWithdrawRefresh.DeferWindowSeconds), "past 5s: elapsed");
        }

        [TestMethod]
        public void Debouncer_HasElapsed_FalseWhenNothingIsPending()
        {
            var debouncer = new MuleRefreshDebouncer();

            Assert.IsFalse(debouncer.HasElapsed(0x50000001, Epoch.AddSeconds(100), MuleWithdrawRefresh.DeferWindowSeconds),
                "an elapsed check must never itself open a window");
            Assert.IsFalse(debouncer.IsPending(0x50000001));
        }

        [TestMethod]
        public void Debouncer_CancelDropsThePendingWindow()
        {
            var debouncer = new MuleRefreshDebouncer();
            debouncer.TryOpenWindow(0x50000001, Epoch);

            Assert.IsTrue(debouncer.Cancel(0x50000001), "cancelling reports a window was pending");
            Assert.IsFalse(debouncer.IsPending(0x50000001));
            Assert.IsFalse(debouncer.HasElapsed(0x50000001, Epoch.AddSeconds(100), MuleWithdrawRefresh.DeferWindowSeconds), "a cancelled window can never elapse");

            Assert.IsTrue(debouncer.TryOpenWindow(0x50000001, Epoch.AddSeconds(1)), "a deferral after the cancel opens a fresh window");
        }

        [TestMethod]
        public void Debouncer_IsPerViewer()
        {
            var debouncer = new MuleRefreshDebouncer();

            Assert.IsTrue(debouncer.TryOpenWindow(0x50000001, Epoch));
            Assert.IsTrue(debouncer.TryOpenWindow(0x50000002, Epoch), "a second grantee on the same mule gets their own window");

            debouncer.Cancel(0x50000001);

            Assert.IsFalse(debouncer.IsPending(0x50000001));
            Assert.IsTrue(debouncer.IsPending(0x50000002), "one viewer's cancel must not affect another viewer's pending window");
        }

        [TestMethod]
        public void Debouncer_Clear_DropsEveryWindow()
        {
            var debouncer = new MuleRefreshDebouncer();

            debouncer.TryOpenWindow(0x50000001, Epoch);
            debouncer.TryOpenWindow(0x50000002, Epoch);

            debouncer.Clear();

            Assert.AreEqual(0, debouncer.PendingCount);
            Assert.IsFalse(debouncer.HasElapsed(0x50000001, Epoch.AddSeconds(100), MuleWithdrawRefresh.DeferWindowSeconds), "a window dropped by Clear can never elapse");
        }
    }
}
