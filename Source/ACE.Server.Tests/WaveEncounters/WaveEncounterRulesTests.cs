using System;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.WaveEncounters;

namespace ACE.Server.Tests.WaveEncounters
{
    /// <summary>
    /// WaveEncounterRules: the pure tick decision, the reap's end test, the start refusals and the
    /// clear-quest rule. Every input is a parameter, so no PropertyManager key is read by this class; the
    /// tunables' fallbacks are pinned separately in <see cref="WaveEncounterTunablesFallbackTests"/>.
    /// </summary>
    [TestClass]
    public class WaveEncounterRulesTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 9, 24, 12, 0, 0, DateTimeKind.Utc);

        // ---- Decide --------------------------------------------------------------------------------------

        [TestMethod]
        public void BeforeWaveOne_SpawnsAsSoonAsItIsDue()
        {
            Assert.AreEqual(WaveTickAction.SpawnWave, WaveEncounterRules.Decide(0, 10, 0, T0, T0));
            Assert.AreEqual(WaveTickAction.Wait, WaveEncounterRules.Decide(0, 10, 0, T0.AddSeconds(1), T0));
        }

        [TestMethod]
        public void AStandingWave_AlwaysWaits_EvenPastItsDueTime()
        {
            // The guard that stops a wave landing on top of the previous one.
            Assert.AreEqual(WaveTickAction.Wait, WaveEncounterRules.Decide(3, 10, 1, T0.AddSeconds(-30), T0));
            Assert.AreEqual(WaveTickAction.Wait, WaveEncounterRules.Decide(10, 10, 1, null, T0));
        }

        [TestMethod]
        public void AClearedWave_BreathesUntilDue_ThenSpawnsTheNext()
        {
            var due = T0.AddSeconds(1);

            Assert.AreEqual(WaveTickAction.Wait, WaveEncounterRules.Decide(4, 10, 0, due, T0.AddMilliseconds(999)));
            Assert.AreEqual(WaveTickAction.SpawnWave, WaveEncounterRules.Decide(4, 10, 0, due, due));
        }

        [TestMethod]
        public void AClearedFinalWave_IsAWin_WhateverTheDueTimeSays()
        {
            Assert.AreEqual(WaveTickAction.Win, WaveEncounterRules.Decide(10, 10, 0, null, T0));
            Assert.AreEqual(WaveTickAction.Win, WaveEncounterRules.Decide(10, 10, 0, T0.AddHours(1), T0));
        }

        [TestMethod]
        public void AnEmptyEarlierWaveWithNoDueTime_SpawnsNow_RatherThanStalling()
        {
            Assert.AreEqual(WaveTickAction.SpawnWave, WaveEncounterRules.Decide(6, 10, 0, null, T0));
        }

        [TestMethod]
        public void NoWavesConfigured_NeverSpawnsAndNeverWins()
        {
            Assert.AreEqual(WaveTickAction.Wait, WaveEncounterRules.Decide(0, 0, 0, T0, T0));
        }

        // ---- ShouldEnd -----------------------------------------------------------------------------------

        private static bool End(bool loaded, bool anchor, double elapsed, double sincePresence, out WaveEndReason reason,
            double ttl = 2700, double grace = 20)
            => WaveEncounterRules.ShouldEnd(loaded, anchor, TimeSpan.FromSeconds(elapsed), TimeSpan.FromSeconds(ttl),
                TimeSpan.FromSeconds(sincePresence), TimeSpan.FromSeconds(grace), out reason);

        [TestMethod]
        public void AHealthyEncounter_DoesNotEnd()
        {
            Assert.IsFalse(End(true, true, 600, 5, out var reason));
            Assert.AreEqual(WaveEndReason.None, reason);
        }

        [TestMethod]
        public void NobodyAliveForTheGrace_EndsAsWiped()
        {
            Assert.IsFalse(End(true, true, 600, 19.9, out _));
            Assert.IsTrue(End(true, true, 600, 20, out var reason));
            Assert.AreEqual(WaveEndReason.Wiped, reason);
        }

        [TestMethod]
        public void PastTheTtl_EndsAsTtl()
        {
            Assert.IsTrue(End(true, true, 2700, 0, out var reason));
            Assert.AreEqual(WaveEndReason.Ttl, reason);
        }

        [TestMethod]
        public void AnUnloadedLandblock_OutranksEveryOtherReason()
        {
            Assert.IsTrue(End(false, false, 99999, 99999, out var reason));
            Assert.AreEqual(WaveEndReason.LandblockUnloaded, reason);
        }

        [TestMethod]
        public void AGoneAnchor_EndsTheEncounter()
        {
            Assert.IsTrue(End(true, false, 10, 0, out var reason));
            Assert.AreEqual(WaveEndReason.AnchorGone, reason);
        }

        [TestMethod]
        public void ZeroTtlAndZeroGrace_DisableThoseTests()
        {
            Assert.IsFalse(End(true, true, 99999, 99999, out _, ttl: 0, grace: 0));
        }

        [TestMethod]
        public void OnlyAWinIsAWin()
        {
            Assert.IsTrue(WaveEncounterRules.IsWin(WaveEndReason.Win));

            foreach (WaveEndReason reason in Enum.GetValues(typeof(WaveEndReason)))
            {
                if (reason != WaveEndReason.Win)
                    Assert.IsFalse(WaveEncounterRules.IsWin(reason), reason.ToString());
            }
        }

        // ---- CheckStart ----------------------------------------------------------------------------------

        [TestMethod]
        public void AFreshAnchor_Starts()
        {
            Assert.AreEqual(WaveStartRefusal.None, WaveEncounterRules.CheckStart(false, null, T0, out _));
            Assert.AreEqual(WaveStartRefusal.None, WaveEncounterRules.CheckStart(false, T0, T0, out _), "a cooldown ending now has ended");
        }

        [TestMethod]
        public void ARunningAnchor_IsRefusedAsRunning_EvenOnCooldown()
        {
            Assert.AreEqual(WaveStartRefusal.Running, WaveEncounterRules.CheckStart(true, T0.AddMinutes(4), T0, out _));
        }

        [TestMethod]
        public void AnAnchorOnCooldown_IsRefused_WithTheTimeLeft()
        {
            var refusal = WaveEncounterRules.CheckStart(false, T0.AddSeconds(250), T0, out var remaining);

            Assert.AreEqual(WaveStartRefusal.Cooldown, refusal);
            Assert.AreEqual(250, remaining.TotalSeconds, 0.001);
        }

        [TestMethod]
        public void CooldownMinutes_RoundUp_AndNeverSayZero()
        {
            Assert.AreEqual(5, WaveEncounterRules.CooldownMinutes(TimeSpan.FromSeconds(300)));
            Assert.AreEqual(5, WaveEncounterRules.CooldownMinutes(TimeSpan.FromSeconds(241)));
            Assert.AreEqual(4, WaveEncounterRules.CooldownMinutes(TimeSpan.FromSeconds(240)));
            Assert.AreEqual(1, WaveEncounterRules.CooldownMinutes(TimeSpan.FromSeconds(1)));
            Assert.AreEqual(1, WaveEncounterRules.CooldownMinutes(TimeSpan.Zero));
        }

        // ---- the clear-quest rule ------------------------------------------------------------------------

        [TestMethod]
        public void ClearQuest_GoesToTheLastCreatureStanding_InTheFinalWaveOnly()
        {
            Assert.IsTrue(WaveEncounterRules.ShouldAssignClearQuest(10, 10, 1, true));

            Assert.IsFalse(WaveEncounterRules.ShouldAssignClearQuest(10, 10, 2, true), "both bosses up: nobody carries it");
            Assert.IsFalse(WaveEncounterRules.ShouldAssignClearQuest(10, 10, 0, true), "nobody left to carry it");
            Assert.IsFalse(WaveEncounterRules.ShouldAssignClearQuest(9, 10, 1, true), "the last trash creature of wave 9 is not the clear");
            Assert.IsFalse(WaveEncounterRules.ShouldAssignClearQuest(10, 10, 1, false), "no clear quest on the anchor");
        }

        // ---- the drum's text -----------------------------------------------------------------------------

        [TestMethod]
        public void SoundingDrumLines_AreTheOwnersVerbatim()
        {
            var text = WaveEncounterText.For(1005611);

            Assert.AreSame(WaveEncounterText.SoundingDrum, text);
            Assert.AreEqual("The Sounding Drum booms, and the deep answers. Ten waves come.", WaveEncounterText.Format(text.Start, total: 10));
            Assert.AreEqual("Wave 3 of 10.", WaveEncounterText.Format(text.Wave, 3, 10));
            Assert.AreEqual("The Deepspeaker and the Drumbreaker come for you.", text.FinalWave);
            Assert.AreEqual("The drum is already sounding.", text.RefuseRunning);
            Assert.AreEqual("The drum skin is still shaking. It will answer again in 4 minutes.", WaveEncounterText.Format(text.RefuseCooldown, minutes: 4));
            Assert.AreEqual("The drum skin is still shaking. It will answer again in 1 minute.", WaveEncounterText.Format(text.RefuseCooldown, minutes: 1));
            Assert.AreEqual("The deep falls silent.", text.Win);
            Assert.AreEqual("The drum goes quiet, and what it called slips back into the dark.", text.Fail);
        }

        [TestMethod]
        public void AnUnknownAnchor_GetsTheDefaultLines()
        {
            Assert.AreSame(WaveEncounterText.Default, WaveEncounterText.For(12345));
        }
    }
}
