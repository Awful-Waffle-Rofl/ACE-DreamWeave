using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Defs;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The 2026-10-18 night round: warded ranged (protect.ward), corrupted priority targets (protect.corrupted) and
    /// spawn portals (protect.portals). Shares the builders in WorldEventHoldTests.
    /// </summary>
    public partial class WorldEventHoldTests
    {
        private const WorldEventCombatKinds Melee = WorldEventCombatKinds.Melee;
        private const WorldEventCombatKinds Missile = WorldEventCombatKinds.Missile;
        private const WorldEventCombatKinds Magic = WorldEventCombatKinds.Magic;

        // ---- the ward ---------------------------------------------------------------------------------------

        [TestMethod]
        public void WardHit_CutsMissileAndMagic_MeleeShatters()
        {
            var reduces = Missile | Magic;

            Assert.AreEqual(25f, WorldEventHoldRules.WardHit(100, Missile, true, 0.75, reduces, Melee, out var o1), 1e-4);
            Assert.AreEqual(WorldEventHoldRules.WardOutcome.Reduced, o1);

            Assert.AreEqual(25f, WorldEventHoldRules.WardHit(100, Magic, true, 0.75, reduces, Melee, out _), 1e-4);

            Assert.AreEqual(100f, WorldEventHoldRules.WardHit(100, Melee, true, 0.75, reduces, Melee, out var o2), "the breaking hit lands in full");
            Assert.AreEqual(WorldEventHoldRules.WardOutcome.Shattered, o2);

            Assert.AreEqual(100f, WorldEventHoldRules.WardHit(100, Missile, false, 0.75, reduces, Melee, out var o3), "ward down: everything hits normally");
            Assert.AreEqual(WorldEventHoldRules.WardOutcome.None, o3);

            // Configurable: magic breaks it too, and only missile is reduced.
            WorldEventHoldRules.WardHit(100, Magic, true, 0.75, Missile, Melee | Magic, out var o4);
            Assert.AreEqual(WorldEventHoldRules.WardOutcome.Shattered, o4);
            Assert.AreEqual(50f, WorldEventHoldRules.WardHit(100, Missile, true, 0.5, Missile, Melee | Magic, out _), 1e-4);
        }

        [TestMethod]
        public void CombatKinds_Parse()
        {
            Assert.IsTrue(WorldEventHoldRules.TryParseCombatKinds(new[] { "missile", "Magic" }, out var k));
            Assert.AreEqual(Missile | Magic, k);
            Assert.IsFalse(WorldEventHoldRules.TryParseCombatKinds(new[] { "fire" }, out _));
            Assert.IsFalse(WorldEventHoldRules.TryParseCombatKinds(new string[0], out _));
            Assert.IsFalse(WorldEventHoldRules.TryParseCombatKinds(null, out _));
        }

        private static (WorldEvent Evt, Creature Monster) BuildWardedMonster(WardDef ward = null)
        {
            var (evt, protect) = BuildTimeScaledRun();
            protect.WaveMix = new WaveMixDef();
            protect.Ward = ward ?? new WardDef();

            var monster = BuildHoldCreature();
            monster.P_WorldEvent = evt;
            WorldEventSpawner.ApplyHoldRole(monster, HoldRole.Ranged, new HoldWavePlan { Type = HoldWaveType.Mixed, HoldDistance = 18, Warded = true });

            return (evt, monster);
        }

        [TestMethod]
        public void Ward_ShattersForTenSeconds_ThenComesBack()
        {
            var (_, monster) = BuildWardedMonster();

            Assert.IsTrue(monster.WorldEventWarded, "a mixed wave's ranged are warded");
            Assert.IsTrue(monster.IsWorldEventWardUp(T0));

            Assert.AreEqual(25f, monster.ApplyWorldEventWard(Missile, 100, T0), 1e-4, "ward up: arrows do a quarter");
            Assert.AreEqual(25f, monster.ApplyWorldEventWard(Magic, 100, T0), 1e-4, "and so do spells");

            Assert.AreEqual(100f, monster.ApplyWorldEventWard(Melee, 100, T0 + 1), "steel shatters it");
            Assert.IsFalse(monster.IsWorldEventWardUp(T0 + 1));
            Assert.IsTrue(monster.WorldEventWardRestorePending, "the restore effect is due when it comes back");

            Assert.AreEqual(100f, monster.ApplyWorldEventWard(Missile, 100, T0 + 10.5), "down for 10 s: everything hits normally");
            Assert.AreEqual(100f, monster.ApplyWorldEventWard(Magic, 100, T0 + 10.9));

            Assert.IsTrue(monster.IsWorldEventWardUp(T0 + 11), "back after 10 s");
            Assert.AreEqual(25f, monster.ApplyWorldEventWard(Missile, 100, T0 + 11), 1e-4);

            // A melee hit while it is down does not extend it.
            monster.ApplyWorldEventWard(Melee, 100, T0 + 20);
            monster.ApplyWorldEventWard(Melee, 100, T0 + 25);
            Assert.IsTrue(monster.IsWorldEventWardUp(T0 + 30.01), "shattered at 20, back at 30");
        }

        [TestMethod]
        public void Ward_SettingsAreTheGoals_AndNothingElseIsWarded()
        {
            var ward = new WardDef { Reduction = 0.5, ShatterSeconds = 4, ReducesKinds = Missile, BreakOnKinds = Melee | Magic };
            var (_, monster) = BuildWardedMonster(ward);

            Assert.AreEqual(100f, monster.ApplyWorldEventWard(Magic, 100, T0), "magic breaks this goal's ward");
            Assert.IsTrue(monster.IsWorldEventWardUp(T0 + 4));
            Assert.AreEqual(50f, monster.ApplyWorldEventWard(Missile, 100, T0 + 4), 1e-4);

            // An ordinary monster, and a ranged one in a goal without a ward.
            var plain = BuildHoldCreature();
            Assert.AreEqual(100f, plain.ApplyWorldEventWard(Missile, 100, T0));

            var (evt, protect) = BuildTimeScaledRun();
            protect.WaveMix = new WaveMixDef();
            var unwarded = BuildHoldCreature();
            unwarded.P_WorldEvent = evt;
            WorldEventSpawner.ApplyHoldRole(unwarded, HoldRole.Ranged, new HoldWavePlan { Type = HoldWaveType.Mixed, Warded = false });
            Assert.IsFalse(unwarded.WorldEventWarded);
            Assert.AreEqual(100f, unwarded.ApplyWorldEventWard(Missile, 100, T0));
        }

        [TestMethod]
        public void Ward_AMeleeSwingThroughTheDamagePipeline_ShattersIt()
        {
            // The damage pipeline reads server properties; this test project has no database (as DamageEventTests).
            DefaultPropertyManager.LoadDefaultProperties();

            var (evt, protect) = BuildTimeScaledRun();
            protect.WaveMix = new WaveMixDef();
            protect.Ward = new WardDef();

            var archer = TestCreatures.CreateDefender(maxHealth: 5000);
            archer.P_WorldEvent = evt;
            archer.WorldEventWarded = true;

            Assert.IsTrue(archer.IsWorldEventWardUp(ACE.Common.Time.GetUnixTime()));

            var swordsman = TestCreatures.CreateAttacker(overpower: true);
            var hit = DamageEvent.CalculateDamage(swordsman, archer, null);

            Assert.IsTrue(hit.HasDamage);
            Assert.IsFalse(archer.IsWorldEventWardUp(ACE.Common.Time.GetUnixTime()), "a melee hit through DamageEvent shatters the ward");
        }

        // ---- corrupted --------------------------------------------------------------------------------------

        [TestMethod]
        public void Corrupted_EmpowerFactor()
        {
            Assert.AreEqual(2.0, WorldEventHoldRules.EmpowerFactor(true, null, 12, 1.5, 2.0), "the corrupted one itself");
            Assert.AreEqual(1.5, WorldEventHoldRules.EmpowerFactor(false, 8, 12, 1.5, 2.0), "inside the aura");
            Assert.AreEqual(1.5, WorldEventHoldRules.EmpowerFactor(false, 12, 12, 1.5, 2.0), "on the edge");
            Assert.AreEqual(1.0, WorldEventHoldRules.EmpowerFactor(false, 12.1, 12, 1.5, 2.0), "outside");
            Assert.AreEqual(1.0, WorldEventHoldRules.EmpowerFactor(false, null, 12, 1.5, 2.0), "no corrupted alive");
            Assert.AreEqual(1.0, WorldEventHoldRules.EmpowerFactor(false, 1, 0, 1.5, 2.0), "radius 0: no aura");
        }

        [TestMethod]
        public void Corrupted_HitsTheTargetsTwiceAsHard_StillUnderTheCap()
        {
            var (evt, protect) = BuildTimeScaledRun();
            protect.WaveMix = new WaveMixDef();
            protect.MinDeathSeconds = 0;   // the cap is checked separately below

            var target = BuildFreshTarget(evt);

            var plain = BuildHoldCreature();
            plain.P_WorldEvent = evt;

            var corrupted = BuildHoldCreature();
            corrupted.P_WorldEvent = evt;
            WorldEventSpawner.ApplyHoldRole(corrupted, HoldRole.Corrupted, new HoldWavePlan { Type = HoldWaveType.Mixed, BreachTargetGuid = 7 });

            Assert.IsTrue(corrupted.WorldEventCorrupted);
            Assert.IsTrue(corrupted.WorldEventSeeker, "a corrupted monster goes for the targets");
            Assert.AreEqual(7u, corrupted.WorldEventSeekAvoidGuid);

            var plainHit = target.ScaleWorldEventProtectedHit(plain, 30f, T0);
            var corruptedHit = BuildFreshTarget(evt).ScaleWorldEventProtectedHit(corrupted, 30f, T0);

            Assert.AreEqual(plainHit * 2, corruptedHit, 2.0, "selfMult 2");

            // With the cap on, a corrupted crowd still cannot beat minDeathSeconds.
            protect.MinDeathSeconds = 60;
            var crowd = SecondsUntilDown(BuildFreshTarget(evt), 12, 2.0, protect, out _, corrupt: true);
            Assert.IsTrue(crowd >= 58, $"twelve corrupted took {crowd} s, the cap is 60");

            Assert.AreEqual(1.0, plain.WorldEventEmpowerFactor(), "no corrupted alive in the spawner: no aura");
        }

        [TestMethod]
        public void Corrupted_Title()
        {
            Assert.AreEqual("Corrupted Shallows Gorger", WorldEventHoldRules.Titled("Corrupted", "Shallows Gorger"));
            Assert.AreEqual("Corrupted Elite Hea Tnakarea", WorldEventHoldRules.Titled("Corrupted", "Elite Hea Tnakarea"), "an elite keeps its own title");
            Assert.AreEqual("Corrupted Shallows Gorger", WorldEventHoldRules.Titled("Corrupted", "Corrupted Shallows Gorger"));
            Assert.AreEqual("Shallows Gorger", WorldEventHoldRules.Titled("", "Shallows Gorger"));
        }

        // ---- portals ----------------------------------------------------------------------------------------

        private static PortalsDef AcidPortals() => new PortalsDef
        {
            Count = 4, Radius = 30, RotationDegrees = 45, Scale = 0.35, Inset = 2, PulseEffect = "PortalExit",
            Parts = new List<DecorDef>
            {
                new DecorDef { Wcid = 1002645, Scale = 6.9f, Speed = 0.8f, Dz = 5f, Dy = 3.14f, Pitch = 90 },
                new DecorDef { Wcid = 1002644, Scale = 5.5f, Speed = 0.941f, Dz = 5f, Dy = 2.30f, Pitch = 90 },
                new DecorDef { Wcid = 1002645, Scale = 1.7f, Speed = 2.0f, Dz = 5f, Dy = -0.03f, Pitch = 90 },
            }
        };

        [TestMethod]
        public void Portals_EvenlyAroundTheOutside_BetweenTheTargets()
        {
            var p = AcidPortals();
            var ring = WorldEventHoldRules.PortalOffsets(p.Count, p.Radius, p.RotationDegrees);

            Assert.AreEqual(4, ring.Count);

            foreach (var o in ring)
                Assert.AreEqual(30.0, Math.Sqrt(o.Dx * o.Dx + o.Dy * o.Dy), 1e-3, "on the 30 m ring, outside the 26 m spawn ring");

            // Bearings 45, 135, 225, 315: northeast first, clockwise.
            Assert.AreEqual(30 * Math.Sin(Math.PI / 4), ring[0].Dx, 1e-3);
            Assert.AreEqual(30 * Math.Cos(Math.PI / 4), ring[0].Dy, 1e-3);
            Assert.AreEqual(30 * Math.Sin(3 * Math.PI / 4), ring[1].Dx, 1e-3);
            Assert.AreEqual(30 * Math.Cos(3 * Math.PI / 4), ring[1].Dy, 1e-3);

            CollectionAssert.AreEqual(new[] { 45.0, 135.0, 225.0, 315.0 },
                WorldEventHoldRules.PortalBearings(4, 45).Select(b => Math.Round(b * 180 / Math.PI, 6)).ToArray());
        }

        [TestMethod]
        public void Portals_PartsScaledDown_OnTheGround_OneSetPerPortal()
        {
            var p = AcidPortals();
            var scaled = WorldEventHoldRules.ScalePortalParts(p.Parts, p.Scale);

            Assert.AreEqual(3, scaled.Count);
            Assert.AreEqual(6.9f * 0.35f, scaled[0].Scale, 1e-4);
            Assert.AreEqual(5f * 0.35f, scaled[0].Dz, 1e-4, "height scaled with the portal");
            Assert.AreEqual(3.14f * 0.35f, scaled[0].Dy, 1e-4);
            Assert.AreEqual(90f, scaled[0].Pitch, "stood upright like the acid portal");
            Assert.IsTrue(scaled.All(s => s.Snap), "on the ground under each portal, not at the anchor's height");

            var placed = WorldEventHoldRules.PlaceTargetDecor(WorldEventHoldRules.PortalOffsets(4, 30, 45), scaled);
            Assert.AreEqual(12, placed.Count, "three parts at each of four portals");

            foreach (var part in placed)
            {
                var dist = Math.Sqrt(part.Dx * part.Dx + part.Dy * part.Dy);
                Assert.IsTrue(dist > 29 && dist < 31.5, $"each part sits at its portal ({dist:F2} m)");
            }
        }

        [TestMethod]
        public void Portals_WavesSpawnAtThem()
        {
            var anchor = new Position(0x00010064, 96f, 96f, 10f, 0f, 0f, 0f, 1f, 0);
            var spawns = WorldEvent.PortalSpawnAnchors(anchor, AcidPortals());

            Assert.AreEqual(4, spawns.Count, "one spawn point per portal; the spawner deals a wave over them round-robin");

            foreach (var s in spawns)
                Assert.AreEqual(28.0, anchor.DistanceTo(s), 0.05, "2 m inside each portal");

            Assert.AreEqual(0, WorldEvent.PortalSpawnAnchors(null, AcidPortals()).Count);
        }

        // ---- settings and the plan ----------------------------------------------------------------------------

        [TestMethod]
        public void GoalsJson_WardCorruptedPortals_Validate()
        {
            void Refused(string with, string expected)
            {
                var json = SampleProtectJson.Replace(@"""radius"": 6.0,", @"""radius"": 6.0, " + with);
                var store = WorldEventAxisStore.Parse(null, null, HoldGoalsJson(json, holdSeconds: 480), null, null);

                Assert.IsFalse(store.Goals.ContainsKey("hold_wards"), with);
                Assert.IsTrue(store.Diagnostics.Any(d => d.Contains(expected)), string.Join(" | ", store.Diagnostics));
            }

            Refused(@"""ward"": { ""reduction"": 1.5 },", "protect.ward.reduction");
            Refused(@"""ward"": { ""shatterSeconds"": 0 },", "protect.ward.shatterSeconds");
            Refused(@"""ward"": { ""breakOn"": [ ""fire"" ] },", "protect.ward.breakOn");
            Refused(@"""ward"": { ""reduces"": [ ] },", "protect.ward.reduces");
            Refused(@"""corrupted"": { ""prefix"": ""Corrupted!"" },", "protect.corrupted.prefix");
            Refused(@"""corrupted"": { ""empowerMult"": 9 },", "protect.corrupted aura");
            Refused(@"""portals"": { ""count"": 9, ""parts"": [ { ""wcid"": 1 } ] },", "protect.portals.count");
            Refused(@"""portals"": { ""count"": 4 },", "protect.portals with no parts");
            Refused(@"""waves"": { ""base"": 6, ""perParticipant"": 0, ""cap"": 6 }, ""minSeconds"": 0, ""waveMix"": { ""minCorrupted"": 3, ""maxCorrupted"": 2 },", "protect.waveMix minimum");

            var ok = SampleProtectJson.Replace(@"""radius"": 6.0,",
                @"""radius"": 6.0, ""ward"": { ""reduces"": [ ""missile"" ], ""breakOn"": [ ""melee"", ""magic"" ], ""announce"": [ ""Warded!"" ] },");
            var store = WorldEventAxisStore.Parse(null, null, HoldGoalsJson(ok, holdSeconds: 480), null, null);

            Assert.IsTrue(store.Goals.ContainsKey("hold_wards"), string.Join(" | ", store.Diagnostics));
            Assert.AreEqual(Missile, store.Goals["hold_wards"].Protect.Ward.ReducesKinds);
            Assert.AreEqual(Melee | Magic, store.Goals["hold_wards"].Protect.Ward.BreakOnKinds);
        }

        [TestMethod]
        public void MixedPlan_HasWardedRangedAndCorrupted()
        {
            using var rangedFamily = RangedFamily();

            var (evt, objective) = MixedRun();
            evt.Composition.Goal.Protect.Ward = new WardDef();
            evt.Spawner.AdoptProtectedForTest(BuildHoldCreature());

            var plan = evt.BuildHoldWavePlan(objective, 2, ElevenTusks());

            Assert.IsTrue(plan.Warded);
            Assert.AreEqual(1, plan.Roles.Count(r => r == HoldRole.Corrupted));
            Assert.AreEqual(3, plan.Roles.Count(r => r == HoldRole.Ranged));

            var (bare, bareObjective) = MixedRun();
            bare.Spawner.AdoptProtectedForTest(BuildHoldCreature());
            Assert.IsFalse(bare.BuildHoldWavePlan(bareObjective, 2, ElevenTusks()).Warded, "no protect.ward: no ward");
        }

        // ---- shipped content --------------------------------------------------------------------------------

        [TestMethod]
        public void ShippedGoals_WardCorruptedPortals_WhenPresent()
        {
            var goalsPath = FindContentFile(Path.Combine("ace-content", "events", "axes", "goals.json"));

            if (goalsPath == null)
                return;

            var store = WorldEventAxisStore.Parse(null, null, File.ReadAllText(goalsPath), null, null);

            foreach (var id in new[] { "defend_envoys" })
            {
                var p = store.Goals[id].Protect;

                Assert.AreEqual(0.3, p.WaveMix.Ranged, 1e-9, id);
                Assert.AreEqual(3, p.WaveMix.MinRanged, id);
                Assert.AreEqual(0.1, p.WaveMix.Corrupted, 1e-9, id);

                Assert.IsNotNull(p.Ward, id);
                Assert.AreEqual(0.75, p.Ward.Reduction, 1e-9, id);
                Assert.AreEqual(10, p.Ward.ShatterSeconds, 1e-9, id);
                Assert.AreEqual(Missile | Magic, p.Ward.ReducesKinds, id);
                Assert.AreEqual(Melee, p.Ward.BreakOnKinds, id);

                Assert.IsNotNull(p.Corrupted, id);
                Assert.AreEqual("Corrupted", p.Corrupted.Prefix, id);
                Assert.AreEqual(1.5, p.Corrupted.Scale, 1e-9, $"{id}: corrupted 1.5x, easy to pick out");
                Assert.AreEqual("VitaeDownBlack", p.Corrupted.Effect, $"{id}: a dark aura, not the purple of a lightning vuln");
                Assert.AreEqual("none", p.Corrupted.ExtraEffect, id);
                Assert.AreEqual(0.75, p.Portals.Scale, 1e-9, $"{id}: portals 1.5x the last 0.5");
                Assert.AreEqual(2, p.Corrupted.AssaultEveryWaves, $"{id}: an assault every other wave");
                Assert.AreEqual(2, p.Corrupted.AssaultFromWave, id);
                Assert.AreEqual(2, p.Corrupted.AssaultCount, $"{id}: two corrupted per assault");
                Assert.AreEqual(3.0, p.Corrupted.SelfMult, 1e-9, $"{id}: an assault monster hits its knight 3x as hard");
                Assert.IsTrue(p.Corrupted.AssaultAnnounce.Count >= 2, id);

                Assert.IsNotNull(p.Portals, id);
                Assert.IsTrue(p.Portals.Count >= 3 && p.Portals.Count <= 5, id);
                Assert.IsTrue(p.Portals.Radius > p.SpawnRadius, $"{id}: portals outside the spawn ring");
                Assert.IsTrue(p.Portals.Parts.All(part => part.Wcid == 1002644 || part.Wcid == 1002645), $"{id}: the acid portal's rings");

                foreach (var template in p.Ward.Announce.Concat(p.Corrupted.Announce).Concat(p.Corrupted.AssaultAnnounce.Select(a => WorldEventHoldRules.AssaultLine(a, "Corrupted Grievver Shredder", "Sir Draithon"))))
                {
                    var line = WorldEventHoldRules.FillLine(template, "the Drudges", "envoy", "envoys", "Holtburg", 3, 3);

                    AssertAscii(WorldEventAnnouncer.Global(line));
                    Assert.IsFalse(line.Contains('{') || line.Contains('}'), line);
                    Assert.IsTrue(SentenceCount(line) <= 3, line);
                    Assert.IsFalse(line.IndexOf("warp", StringComparison.OrdinalIgnoreCase) >= 0, line);
                }

                Assert.IsTrue(p.Ward.Announce.Contains("Warded archers hold the ridge. Break their wards with steel!"), id);
                Assert.IsTrue(p.Corrupted.Announce.Contains("One of {family} is corrupted and empowers the horde. Kill it first!"), id);
            }
        }
    }
}
