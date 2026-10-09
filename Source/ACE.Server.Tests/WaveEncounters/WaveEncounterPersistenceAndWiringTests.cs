using System;
using System.Collections.Generic;
using System.IO;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database.Models.Shard;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.WaveEncounters;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests.WaveEncounters
{
    /// <summary>
    /// The three persistence / wiring guarantees of the wave runner, each with a control:
    ///   - a creature stamped with PropertyInt.WaveEncounterId is never saved to the shard;
    ///   - WaveEncounterOrphanFilter.Partition drops any shard biota carrying the stamp;
    ///   - the source wiring: Creature.Die carries the two-key hook, WorldObject.OnActivate gates the start
    ///     on PropertyBool.WaveEncounterAnchor (never on 9031 alone), the landblock load runs the sweep and
    ///     the world tick drives the manager.
    /// Plus the WaveEncounter state machine's death bookkeeping. No PropertyManager key is read.
    /// </summary>
    [TestClass]
    public class WaveEncounterPersistenceAndWiringTests
    {
        private static uint nextGuid = ObjectGuid.DynamicMin + 0x00A10000;

        [TestInitialize]
        public void Init() => TestGameTables.EnsureInitialized();

        private static Creature MakeCreature()
            => new Creature(new Weenie { WeenieClassId = 1005612, WeenieType = WeenieType.Creature }, new ObjectGuid(nextGuid++));

        // ---- persistence -----------------------------------------------------------------------------------

        [TestMethod]
        public void StampedCreature_IsNeverPersistedToTheShard()
        {
            var creature = MakeCreature();
            creature.SetProperty(PropertyInt.WaveEncounterId, 7);

            Assert.IsFalse(creature.IsDynamicThatShouldPersistToShard(), "a wave-encounter creature must never be written to the shard");
        }

        [TestMethod]
        public void TheSameCreatureUnstamped_WouldHaveBeenPersisted()
        {
            // Control: without this the test above passes for reasons unrelated to the guard.
            Assert.IsTrue(MakeCreature().IsDynamicThatShouldPersistToShard());
        }

        // ---- orphan filter (EF-shaped ACE.Database.Models.Shard.Biota, NOT ACE.Entity.Models.Biota) ---------

        private static ACE.Database.Models.Shard.Biota MakeBiota(uint id, params (ushort type, int value)[] ints)
        {
            var biota = new ACE.Database.Models.Shard.Biota { Id = id, WeenieClassId = 1, WeenieType = 10 };

            foreach (var (type, value) in ints)
                biota.BiotaPropertiesInt.Add(new BiotaPropertiesInt { ObjectId = id, Type = type, Value = value });

            return biota;
        }

        private const ushort Stamp = (ushort)PropertyInt.WaveEncounterId;

        [TestMethod]
        public void Partition_DropsStampedBiotas_WhateverTheValue_AndKeepsTheRest()
        {
            var keptA = MakeBiota(0x80000001, (25, 320));
            var orphanA = MakeBiota(0x80000002, (Stamp, 4));
            var orphanB = MakeBiota(0x80000003, (25, 345), (Stamp, 0));
            var keptB = MakeBiota(0x80000004, ((ushort)PropertyInt.MlDigsiteEncounterId - 1, 1));

            var dynamics = new List<ACE.Database.Models.Shard.Biota> { keptA, orphanA, orphanB, keptB };

            WaveEncounterOrphanFilter.Partition(dynamics, out var kept, out var orphanIds);

            CollectionAssert.AreEqual(new List<ACE.Database.Models.Shard.Biota> { keptA, keptB }, kept);
            CollectionAssert.AreEqual(new List<uint> { 0x80000002, 0x80000003 }, orphanIds);
        }

        [TestMethod]
        public void Partition_WithNoOrphans_HandsBackTheSameList()
        {
            var dynamics = new List<ACE.Database.Models.Shard.Biota> { MakeBiota(0x80000011, (25, 1)) };

            WaveEncounterOrphanFilter.Partition(dynamics, out var kept, out var orphanIds);

            Assert.AreSame(dynamics, kept);
            Assert.AreEqual(0, orphanIds.Count);
        }

        [TestMethod]
        public void DigsiteStamp_IsNotAWaveEncounterOrphan_AndViceVersa()
        {
            // The two sweeps are siblings with distinct markers; neither may eat the other's objects.
            var digsite = MakeBiota(0x80000021, ((ushort)PropertyInt.MlDigsiteEncounterId, 3));
            var wave = MakeBiota(0x80000022, (Stamp, 3));

            Assert.IsFalse(WaveEncounterOrphanFilter.IsWaveEncounterOrphan(digsite));
            Assert.IsTrue(WaveEncounterOrphanFilter.IsWaveEncounterOrphan(wave));
            Assert.IsFalse(ACE.Server.MlDigsite.MlDigsiteOrphanFilter.IsDigsiteOrphan(wave));
        }

        // ---- source wiring -----------------------------------------------------------------------------------

        private static string Source(params string[] relative)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            var parts = new List<string> { "Source" };
            parts.AddRange(relative);
            var tail = Path.Combine(parts.ToArray());

            while (dir != null && !File.Exists(Path.Combine(dir.FullName, tail)))
                dir = dir.Parent;

            Assert.IsNotNull(dir, $"Could not find {tail} by walking up from {AppContext.BaseDirectory}");

            return File.ReadAllText(Path.Combine(dir.FullName, tail)).Replace("\r\n", "\n");
        }

        private static string MethodBody(string source, string signature)
        {
            var start = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, $"signature not found: {signature}");

            var open = source.IndexOf('{', start);
            var depth = 0;

            for (var i = open; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}' && --depth == 0)
                    return source.Substring(open, i - open + 1);
            }

            Assert.Fail($"unbalanced body for {signature}");
            return null;
        }

        [TestMethod]
        public void Die_CarriesTheTwoKeyWaveEncounterHook()
        {
            var die = MethodBody(Source("ACE.Server", "WorldObjects", "Creature_Death.cs"),
                "protected virtual void Die(DamageHistoryInfo lastDamager, DamageHistoryInfo topDamager)");

            StringAssert.Contains(die,
                "if (P_WaveEncounter != null && GetProperty(PropertyInt.WaveEncounterId) != null)\n                ACE.Server.WaveEncounters.WaveEncounterManager.OnEncounterCreatureDied(this);");

            // After the exactly-once latch, like every sibling hook.
            Assert.IsTrue(die.IndexOf("dieEntered = true;", StringComparison.Ordinal) < die.IndexOf("WaveEncounterManager.OnEncounterCreatureDied", StringComparison.Ordinal));
        }

        [TestMethod]
        public void OnActivate_GatesTheStartOnTheAnchorBool_NotOnTheRosterBase()
        {
            var activate = MethodBody(Source("ACE.Server", "WorldObjects", "WorldObject_Use.cs"),
                "public virtual void OnActivate(WorldObject activator)");

            StringAssert.Contains(activate,
                "if (player != null && GetProperty(PropertyBool.WaveEncounterAnchor) == true)\n                ACE.Server.WaveEncounters.WaveEncounterManager.TryStart(this, player);");

            Assert.IsFalse(activate.Contains("GetProperty(PropertyInt.WaveChallengeRosterBaseWcid)"), "the hook must never key on 9031: the Proving Grounds portal carries it too");

            // After the use-requirements gate, so a locked or refused anchor cannot start anything.
            Assert.IsTrue(activate.IndexOf("CheckUseRequirements(activator)", StringComparison.Ordinal) < activate.IndexOf("WaveEncounterManager.TryStart", StringComparison.Ordinal));
        }

        [TestMethod]
        public void PersistenceExclusion_And_LandblockSweep_And_WorldTick_AreWired()
        {
            StringAssert.Contains(Source("ACE.Server", "WorldObjects", "WorldObject_Database.cs"),
                "if (GetProperty(PropertyInt.WaveEncounterId) != null)\n                return false;");

            StringAssert.Contains(Source("ACE.Server", "Entity", "Landblock.cs"),
                "dynamics = WaveEncounters.WaveEncounterOrphanFilter.FilterAndSweep(Id.Landblock, Instance, dynamics);");

            var world = Source("ACE.Server", "Managers", "WorldManager.cs");
            var tick = world.IndexOf("ACE.Server.WaveEncounters.WaveEncounterManager.Tick();", StringComparison.Ordinal);

            Assert.IsTrue(tick > 0, "WaveEncounterManager.Tick is not called from WorldManager");
            Assert.IsTrue(world.IndexOf("LandblockManager.Tick(", StringComparison.Ordinal) < tick, "the wave tick must run AFTER LandblockManager.Tick");
        }

        // ---- WaveEncounter bookkeeping -------------------------------------------------------------------------

        private static readonly DateTime T0 = new DateTime(2026, 9, 24, 12, 0, 0, DateTimeKind.Utc);

        private static WaveEncounter MakeEncounter(int totalWaves = 10, string clearQuest = "BluespireLadderD6Cleared")
            => new WaveEncounter(1, new WaveAnchorKey(0x7003FFFF, 1), null, null, totalWaves, 1006510,
                TimeSpan.FromSeconds(1), clearQuest, "tester", T0);

        [TestMethod]
        public void ClearingAWave_ArmsTheNextOneAfterTheBreather()
        {
            var encounter = MakeEncounter();
            var a = MakeCreature();
            var b = MakeCreature();

            encounter.BeginWave(3);
            encounter.TrackAlive(a);
            encounter.TrackAlive(b);

            Assert.IsTrue(encounter.NoteDeath(a.Guid.Full, T0.AddSeconds(10), out _));
            Assert.IsNull(encounter.NextWaveDue, "one creature still stands");

            Assert.IsTrue(encounter.NoteDeath(b.Guid.Full, T0.AddSeconds(20), out var carrier));
            Assert.AreEqual(T0.AddSeconds(21), encounter.NextWaveDue);
            Assert.IsNull(carrier, "wave 3 is not the final wave");

            Assert.IsFalse(encounter.NoteDeath(b.Guid.Full, T0.AddSeconds(30), out _), "a second report of the same death is ignored");
        }

        [TestMethod]
        public void FinalWave_HandsTheClearQuestToTheLastCreatureStanding_Once()
        {
            var encounter = MakeEncounter();
            var caster = MakeCreature();
            var melee = MakeCreature();

            encounter.BeginWave(10);
            encounter.TrackAlive(caster);
            encounter.TrackAlive(melee);

            Assert.IsNull(encounter.TakeClearQuestCarrierAfterSpawn(), "both bosses up: nobody carries it yet");

            Assert.IsTrue(encounter.NoteDeath(melee.Guid.Full, T0, out var carrier));
            Assert.AreSame(caster, carrier, "the survivor carries the clear");
            Assert.IsNull(encounter.NextWaveDue, "no wave after the final one");

            Assert.IsTrue(encounter.NoteDeath(caster.Guid.Full, T0, out var again));
            Assert.IsNull(again, "handed out once only");
            Assert.AreEqual(0, encounter.AliveCount);
        }

        [TestMethod]
        public void FinalWave_WithNoClearQuest_HandsNothingOut()
        {
            var encounter = MakeEncounter(clearQuest: null);
            var a = MakeCreature();
            var b = MakeCreature();

            encounter.BeginWave(10);
            encounter.TrackAlive(a);
            encounter.TrackAlive(b);

            Assert.IsTrue(encounter.NoteDeath(a.Guid.Full, T0, out var carrier));
            Assert.IsNull(carrier);
        }

        [TestMethod]
        public void AfterTheEnd_NothingIsRecordedOrAdopted()
        {
            var encounter = MakeEncounter();
            var a = MakeCreature();

            encounter.BeginWave(1);
            Assert.IsTrue(encounter.Adopt(a));
            encounter.TrackAlive(a);

            Assert.IsTrue(encounter.MarkEnded(WaveEndReason.Wiped));
            Assert.IsFalse(encounter.MarkEnded(WaveEndReason.Ttl), "the end latch fires once");

            Assert.IsFalse(encounter.NoteDeath(a.Guid.Full, T0, out _));
            Assert.IsFalse(encounter.Adopt(MakeCreature()), "a spawn in flight after the end must be refused");
            Assert.AreEqual(1, encounter.SpawnedSnapshot().Count);
        }
    }
}
