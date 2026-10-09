using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Pvp;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>
    /// PropertyManager.Get* throws in ACE.Server.Tests for any uncached key, so PvpTunables.DialSource
    /// must catch that and fall back to the compiled defaults, following FacetTunables' own precedent
    /// (Player_Facets.cs). This pins that fallback path and that it agrees with PvpTunables.Defaults.
    /// </summary>
    [TestClass]
    public class PvpTunablesFallbackTests
    {
        [TestMethod]
        public void DialSource_InTestEnvironment_FallsBackToDefaults()
        {
            var dials = PvpTunables.DialSource();

            Assert.AreEqual(PvpTunables.Defaults, dials);
        }

        [TestMethod]
        public void Defaults_MatchTheDesignDocTable()
        {
            var d = PvpTunables.Defaults;

            Assert.IsTrue(d.Enabled);
            Assert.IsTrue(d.OneVOneEnabled);
            Assert.IsTrue(d.TwoVTwoEnabled);
            Assert.IsTrue(d.FfaEnabled);
            Assert.AreEqual(20, d.AcceptSeconds);
            Assert.AreEqual(30, d.StagingTimeoutSeconds);
            Assert.AreEqual(10, d.CountdownSeconds);
            Assert.AreEqual(300, d.TimeLimitSeconds1v1);
            Assert.AreEqual(300, d.TimeLimitSeconds2v2);
            Assert.AreEqual(300, d.TimeLimitSecondsFfa);
            Assert.AreEqual(10, d.PostMatchSeconds);
            Assert.AreEqual(10, d.MaxConcurrentMatches);
            Assert.AreEqual(50, d.MinLevel);
            Assert.IsTrue(d.BlockSameIp);
            Assert.IsFalse(d.FriendlyFire);
            Assert.IsTrue(d.DeathKeepsEnchantments);
            Assert.IsTrue(d.RetireCombatPets);
            Assert.IsTrue(d.SuppressClassAbilities);
            Assert.IsTrue(d.SuppressEquipmentMods);
            Assert.IsTrue(d.SuppressWeaponMods);
            Assert.IsTrue(d.SuppressPickupBoons);
            Assert.IsTrue(d.SuppressTurnSpeed);
            Assert.AreEqual(120, d.DeclineLockoutSeconds);
            Assert.AreEqual(100, d.MmWindowInitial);
            Assert.AreEqual(50, d.MmWindowGrowthPerMinute);
            Assert.AreEqual(400, d.MmWindowMax);
            Assert.AreEqual(120, d.DuoVsSoloAfterSeconds);
            Assert.AreEqual(5, d.FfaTargetPlayers);
            Assert.AreEqual(5, d.FfaMinPlayers);
            Assert.AreEqual(15, d.FfaMaxPlayers);
            Assert.AreEqual(60, d.FfaMinDecaySeconds);
            Assert.AreEqual(1500, d.RatingInitial);
            Assert.AreEqual(40, d.RatingKProvisional);
            Assert.AreEqual(24, d.RatingKEstablished);
            Assert.AreEqual(32, d.RatingKFfa);
            Assert.AreEqual(10, d.RatingProvisionalGames);
            Assert.AreEqual(14, d.RatingDecayGraceDays);
            Assert.AreEqual(25, d.RatingDecayPointsPerWeek);
            Assert.AreEqual(1500, d.RatingDecayFloor);
            Assert.IsTrue(d.CrierEnabled);
            Assert.IsTrue(d.CrierAnnounceOnJoin);
            Assert.AreEqual(900, d.CrierIntervalSeconds);
            Assert.AreEqual(15, d.CrierLastCallDelaySeconds);
            Assert.AreEqual(120, d.CrierLastCallCooldownSeconds);
            Assert.AreEqual("Arena Crier", d.CrierSenderName);
            Assert.AreEqual(1.0, d.DmgMod1v1);
            Assert.AreEqual(150, d.HealkitSkillCap1v1);
            Assert.AreEqual(1.5, d.HealkitRestorationCap1v1);
            Assert.IsTrue(d.OvertimeEnabled);
            Assert.AreEqual(180, d.OvertimeSeconds);
            Assert.AreEqual(0.0, d.OvertimeHealingMod);
            Assert.AreEqual(0.0, d.OvertimeDamageRampPerMinute);
            Assert.IsTrue(d.BloodEnabled, "owner ruling 2026-10-08: the arena currency is ON by default");
            Assert.AreEqual(1.0, d.FfaRingDmg);
            Assert.AreEqual(2, d.BloodWin);
            Assert.AreEqual(1, d.BloodLoss);
            Assert.AreEqual(1, d.BloodDraw);
            Assert.AreEqual(10, d.BloodDailyCap);
            Assert.AreEqual("America/New_York", d.BloodResetTimezone);
            Assert.AreEqual(0, d.BloodResetHour);
        }

        [TestMethod]
        public void ClampAcceptSeconds_WithinRange_IsUnchanged()
        {
            Assert.AreEqual(20, PvpTunables.ClampAcceptSeconds(20));
        }

        [TestMethod]
        public void ClampAcceptSeconds_AboveThirty_ClampsToThirty()
        {
            // pvp_arena_accept_seconds "must stay at or under 30, because the popup times out at 30 seconds".
            Assert.AreEqual(30, PvpTunables.ClampAcceptSeconds(999));
        }

        [TestMethod]
        public void ClampAcceptSeconds_AtThirty_IsUnchanged()
        {
            Assert.AreEqual(30, PvpTunables.ClampAcceptSeconds(30));
        }

        [TestMethod]
        public void ClampAcceptSeconds_BelowFive_ClampsToFive()
        {
            Assert.AreEqual(5, PvpTunables.ClampAcceptSeconds(0));
        }

        [TestMethod]
        public void ClampAcceptSeconds_Negative_ClampsToFive()
        {
            Assert.AreEqual(5, PvpTunables.ClampAcceptSeconds(-100));
        }

        [TestMethod]
        public void ClampAcceptSeconds_AtFive_IsUnchanged()
        {
            Assert.AreEqual(5, PvpTunables.ClampAcceptSeconds(5));
        }
    }
}
