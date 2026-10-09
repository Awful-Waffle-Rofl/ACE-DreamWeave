using System;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Managers;
using ACE.Server.WaveEncounters;

namespace ACE.Server.Tests.WaveEncounters
{
    /// <summary>
    /// The four wave-encounter tunables: each key must be registered in PropertyManager's default
    /// dictionaries (ModifyLong/ModifyDouble return false for an unregistered key, which is also what the
    /// config-metadata / config-defaults generators reflect off), and WaveEncounterTunables must read the
    /// live value. Every key this class touches is SEEDED here and restored to its shipped default in a
    /// finally, so the class passes alone and in any order.
    /// </summary>
    [TestClass]
    public class WaveEncounterTunablesFallbackTests
    {
        [TestMethod]
        public void EveryTunable_IsRegistered_AndReadLive()
        {
            try
            {
                Assert.IsTrue(PropertyManager.ModifyLong("wave_encounter_ttl_seconds", 1234), "wave_encounter_ttl_seconds is not in DefaultLongProperties");
                Assert.IsTrue(PropertyManager.ModifyLong("wave_encounter_wipe_grace_seconds", 7), "wave_encounter_wipe_grace_seconds is not in DefaultLongProperties");
                Assert.IsTrue(PropertyManager.ModifyLong("wave_encounter_cooldown_seconds", 90), "wave_encounter_cooldown_seconds is not in DefaultLongProperties");
                Assert.IsTrue(PropertyManager.ModifyDouble("wave_encounter_presence_radius", 55.5), "wave_encounter_presence_radius is not in DefaultDoubleProperties");

                Assert.AreEqual(TimeSpan.FromSeconds(1234), WaveEncounterTunables.Ttl);
                Assert.AreEqual(TimeSpan.FromSeconds(7), WaveEncounterTunables.WipeGrace);
                Assert.AreEqual(TimeSpan.FromSeconds(90), WaveEncounterTunables.Cooldown);
                Assert.AreEqual(55.5f, WaveEncounterTunables.PresenceRadius, 0.0001f);
            }
            finally
            {
                Restore();
            }
        }

        [TestMethod]
        public void NegativeValues_ReadAsOff_NotAsANegativeSpan()
        {
            try
            {
                Assert.IsTrue(PropertyManager.ModifyLong("wave_encounter_cooldown_seconds", -5));
                Assert.IsTrue(PropertyManager.ModifyDouble("wave_encounter_presence_radius", -1));

                Assert.AreEqual(TimeSpan.Zero, WaveEncounterTunables.Cooldown);
                Assert.AreEqual(0f, WaveEncounterTunables.PresenceRadius);
            }
            finally
            {
                Restore();
            }
        }

        [TestMethod]
        public void ShippedDefaults_MatchTheOwnerRuling()
        {
            // The registered default (what a fresh shard reads) must agree with the code's own fallback
            // (what a failed read gets), or the two paths would run different encounters.
            Assert.AreEqual(WaveEncounterTunables.DefaultTtlSeconds, DefaultPropertyManager.DefaultLongProperties["wave_encounter_ttl_seconds"].Item);
            Assert.AreEqual(WaveEncounterTunables.DefaultWipeGraceSeconds, DefaultPropertyManager.DefaultLongProperties["wave_encounter_wipe_grace_seconds"].Item);
            Assert.AreEqual(WaveEncounterTunables.DefaultCooldownSeconds, DefaultPropertyManager.DefaultLongProperties["wave_encounter_cooldown_seconds"].Item);
            Assert.AreEqual(WaveEncounterTunables.DefaultPresenceRadius, DefaultPropertyManager.DefaultDoubleProperties["wave_encounter_presence_radius"].Item);

            // And the owner's numbers (2026-09-24).
            Assert.AreEqual(2700, DefaultPropertyManager.DefaultLongProperties["wave_encounter_ttl_seconds"].Item);
            Assert.AreEqual(20, DefaultPropertyManager.DefaultLongProperties["wave_encounter_wipe_grace_seconds"].Item);
            Assert.AreEqual(300, DefaultPropertyManager.DefaultLongProperties["wave_encounter_cooldown_seconds"].Item);
            Assert.AreEqual(80.0, DefaultPropertyManager.DefaultDoubleProperties["wave_encounter_presence_radius"].Item);
        }

        private static void Restore()
        {
            PropertyManager.ModifyLong("wave_encounter_ttl_seconds", WaveEncounterTunables.DefaultTtlSeconds);
            PropertyManager.ModifyLong("wave_encounter_wipe_grace_seconds", WaveEncounterTunables.DefaultWipeGraceSeconds);
            PropertyManager.ModifyLong("wave_encounter_cooldown_seconds", WaveEncounterTunables.DefaultCooldownSeconds);
            PropertyManager.ModifyDouble("wave_encounter_presence_radius", WaveEncounterTunables.DefaultPresenceRadius);
        }
    }
}
