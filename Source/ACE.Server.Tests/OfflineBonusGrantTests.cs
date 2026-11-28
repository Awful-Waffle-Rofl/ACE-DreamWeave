using System;
using System.Runtime.CompilerServices;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Common;
using ACE.Server.Managers;
using ACE.Server.Tests.Pvp;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Granted offline bonus time on a real (seeded) Player (owner ruling 2026-11-25, the Allegiance Blessing): the grant may
    /// take the bank past offline_bonus_max_seconds, and none of the bank's readers or writers - the heartbeat reconcile
    /// (UpdateOfflineBonus), the projection (GetOfflineExperienceBonusRemaining) and the login accrual (AccrueOfflineBonus) -
    /// ever clamps it back down. Every key the path reads is seeded and restored.
    /// </summary>
    [TestClass]
    public class OfflineBonusGrantTests
    {
        private const long Cap = 86400;

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "offlineBonusLastCheck")]
        private static extern ref double LastCheck(Player player);

        [ClassInitialize]
        public static void ClassSetup(TestContext context) => ThornsCloseRangeTests.EnsurePlayerTypeInitializable();

        private static void WithOfflineKeys(Action body, bool enabled = true)
        {
            try
            {
                Assert.IsTrue(PropertyManager.ModifyBool("offline_bonus_enabled", enabled));
                Assert.IsTrue(PropertyManager.ModifyLong("offline_bonus_max_seconds", Cap));
                Assert.IsTrue(PropertyManager.ModifyLong("offline_bonus_idle_timeout_seconds", 300));
                Assert.IsTrue(PropertyManager.ModifyDouble("offline_bonus_idle_accrual_rate", 1.0, init: true));
                Assert.IsTrue(PropertyManager.ModifyDouble("offline_bonus_multiplier", 1.0, init: true));
                body();
            }
            finally
            {
                // Restored to the compiled defaults: an unseeded read would hit the database, so nothing is read before seeding.
                PropertyManager.ModifyBool("offline_bonus_enabled", DefaultPropertyManager.DefaultBooleanProperties["offline_bonus_enabled"].Item);
                foreach (var k in new[] { "offline_bonus_max_seconds", "offline_bonus_idle_timeout_seconds" })
                    PropertyManager.ModifyLong(k, DefaultPropertyManager.DefaultLongProperties[k].Item);
                foreach (var k in new[] { "offline_bonus_idle_accrual_rate", "offline_bonus_multiplier" })
                    PropertyManager.ModifyDouble(k, DefaultPropertyManager.DefaultDoubleProperties[k].Item, init: true);
            }
        }

        [TestMethod]
        public void Grant_AddsExactlyTheSeconds_BelowAndAboveTheCap() => WithOfflineKeys(() =>
        {
            var p = PvpTemplatePlayerTests.SeededPlayer();

            Assert.AreEqual(3600.0, p.AddOfflineBonusSeconds(3600));
            Assert.AreEqual(3600.0, p.OfflineExperienceBonusRemaining, "an empty bank gains exactly the grant");

            p.OfflineExperienceBonusRemaining = Cap;
            Assert.AreEqual(Cap + 3600.0, p.AddOfflineBonusSeconds(3600), "a bank at the cap goes past it");
            Assert.AreEqual(Cap + 3600.0, p.OfflineExperienceBonusRemaining);

            Assert.AreEqual(Cap + 7200.0, p.AddOfflineBonusSeconds(3600), "and further past it on a second grant");
            Assert.AreEqual(Cap + 7200.0, p.AddOfflineBonusSeconds(0), "a zero grant changes nothing");
        });

        /// <summary>The heartbeat reconcile over an idle interval accrues, and must leave an above-cap bank where it is.</summary>
        [TestMethod]
        public void HeartbeatReconcile_IdleInterval_DoesNotClampAnAboveCapBankDown() => WithOfflineKeys(() =>
        {
            var p = PvpTemplatePlayerTests.SeededPlayer();
            p.OfflineExperienceBonusRemaining = Cap + 3600;
            LastCheck(p) = Time.GetUnixTime() - 600; // ten idle minutes since the last reconcile (no combat this session)

            p.UpdateOfflineBonus();

            Assert.AreEqual(Cap + 3600.0, p.OfflineExperienceBonusRemaining, "idle accrual adds nothing over the cap and takes nothing away");

            // Control: below the cap, the same idle interval accrues up to the cap.
            var q = PvpTemplatePlayerTests.SeededPlayer();
            q.OfflineExperienceBonusRemaining = Cap - 100;
            LastCheck(q) = Time.GetUnixTime() - 600;
            q.UpdateOfflineBonus();
            Assert.AreEqual((double)Cap, q.OfflineExperienceBonusRemaining, "control: accrual below the cap still stops at the cap");
        });

        [TestMethod]
        public void Projection_DoesNotClampAnAboveCapBankDown() => WithOfflineKeys(() =>
        {
            var p = PvpTemplatePlayerTests.SeededPlayer();
            p.OfflineExperienceBonusRemaining = Cap + 3600;
            LastCheck(p) = Time.GetUnixTime() - 600;

            Assert.AreEqual(Cap + 3600.0, p.GetOfflineExperienceBonusRemaining());
            Assert.IsTrue(p.IsOfflineExperienceBonusActive);
        });

        [TestMethod]
        public void LoginAccrual_DoesNotClampAnAboveCapBankDown() => WithOfflineKeys(() =>
        {
            var p = PvpTemplatePlayerTests.SeededPlayer();
            p.OfflineExperienceBonusRemaining = Cap + 3600;
            p.LogoffTimestamp = Time.GetUnixTime() - 7200; // two hours offline

            p.AccrueOfflineBonus();

            Assert.AreEqual(Cap + 3600.0, p.OfflineExperienceBonusRemaining, "the login accrual keeps the granted time");
        });

        /// <summary>Only active combat drains: a grant made while fighting goes down 1:1 with active time, through the cap.</summary>
        [TestMethod]
        public void ActiveCombat_DrainsAGrantedBank() => WithOfflineKeys(() =>
        {
            var p = PvpTemplatePlayerTests.SeededPlayer();
            p.OfflineExperienceBonusRemaining = Cap + 3600;
            Assert.AreEqual(2000L, p.ApplyOfflineExperienceBonus(1000), "a kill now: boosted, and the active window opens");
            LastCheck(p) = Time.GetUnixTime() - 120; // two minutes inside the active window

            p.UpdateOfflineBonus();

            var bank = p.OfflineExperienceBonusRemaining;
            Assert.IsTrue(bank < Cap + 3600.0 && bank >= Cap + 3600.0 - 125, $"drained by about two minutes of combat, got {bank}");
        });
    }
}
