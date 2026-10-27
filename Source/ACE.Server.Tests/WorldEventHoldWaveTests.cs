using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The Hold design pass: special waves (breach, runners, ranged, hunters, finale), heal reduction after a
    /// hit, the target layout by crowd size, and the goals.json settings behind them. Shares the builders in
    /// WorldEventHoldTests.
    /// </summary>
    public partial class WorldEventHoldTests
    {
        private static SpecialWaveDef Special(HoldWaveType type, double weight = 1.0) =>
            new SpecialWaveDef { Type = type.ToString().ToLowerInvariant(), TypeKind = type, Weight = weight };

        private static List<SpecialWaveDef> AllSpecials() => new List<SpecialWaveDef>
        {
            Special(HoldWaveType.Breach), Special(HoldWaveType.Runners), Special(HoldWaveType.Ranged),
            Special(HoldWaveType.Hunters), Special(HoldWaveType.Finale)
        };

        // ---- wave type --------------------------------------------------------------------------------------

        [TestMethod]
        public void PickWaveType_LastWave_IsAlwaysTheFinale()
        {
            var rng = new Random(1);

            Assert.AreEqual(HoldWaveType.Finale, WorldEventHoldRules.PickWaveType(4, 4, HoldWaveType.Normal, 0, 2, AllSpecials(), rng, null));
            Assert.AreEqual(HoldWaveType.Finale, WorldEventHoldRules.PickWaveType(4, 4, HoldWaveType.Normal, 1, 2, AllSpecials(), rng, HoldWaveType.Breach),
                "a forced type never replaces the finale");
        }

        [TestMethod]
        public void PickWaveType_NoFinaleDefined_LastWaveRollsLikeAnyOther()
        {
            var types = AllSpecials().Where(t => t.TypeKind != HoldWaveType.Finale).ToList();

            Assert.AreEqual(HoldWaveType.Normal, WorldEventHoldRules.PickWaveType(4, 4, HoldWaveType.Normal, 0, 2, types, new Random(1), null));
        }

        [TestMethod]
        public void PickWaveType_ForcedType_AppliesToEveryOtherWave_IncludingTheFirst()
        {
            for (var wave = 1; wave < 4; wave++)
                Assert.AreEqual(HoldWaveType.Runners,
                    WorldEventHoldRules.PickWaveType(wave, 4, HoldWaveType.Runners, 0, 2, AllSpecials(), new Random(wave), HoldWaveType.Runners),
                    $"wave {wave}: --wave-type wins over the chance, the start wave and the no-repeat rule");
        }

        [TestMethod]
        public void PickWaveType_BeforeTheStartWave_OrWithNoChance_IsOrdinary()
        {
            Assert.AreEqual(HoldWaveType.Normal, WorldEventHoldRules.PickWaveType(1, 6, HoldWaveType.Normal, 1, 2, AllSpecials(), new Random(1), null),
                "wave 1 is never special with specialWaveFromWave 2");

            for (var seed = 0; seed < 50; seed++)
                Assert.AreEqual(HoldWaveType.Normal,
                    WorldEventHoldRules.PickWaveType(3, 6, HoldWaveType.Normal, 0, 2, AllSpecials(), new Random(seed), null), "chance 0");

            Assert.AreEqual(HoldWaveType.Normal, WorldEventHoldRules.PickWaveType(3, 6, HoldWaveType.Normal, 1, 2, null, new Random(1), null),
                "no specialWaves at all");
        }

        [TestMethod]
        public void PickWaveType_NeverRepeatsTheSameSpecialType_AndNeverRollsTheFinale()
        {
            var rng = new Random(7);
            var previous = HoldWaveType.Normal;
            var seen = new HashSet<HoldWaveType>();

            for (var i = 0; i < 2000; i++)
            {
                var type = WorldEventHoldRules.PickWaveType(3, 10, previous, 1, 2, AllSpecials(), rng, null);

                Assert.AreNotEqual(HoldWaveType.Finale, type, "the finale only comes on the last wave");
                Assert.AreNotEqual(HoldWaveType.Normal, type, "chance 1 with types to spare is always special");

                if (previous != HoldWaveType.Normal)
                    Assert.AreNotEqual(previous, type, "never the same type twice in a row");

                seen.Add(type);
                previous = type;
            }

            Assert.AreEqual(4, seen.Count, "every special type comes up");
        }

        [TestMethod]
        public void PickWaveType_OnlyTypeWasJustUsed_FallsBackToOrdinary()
        {
            var types = new List<SpecialWaveDef> { Special(HoldWaveType.Breach) };

            Assert.AreEqual(HoldWaveType.Normal, WorldEventHoldRules.PickWaveType(3, 6, HoldWaveType.Breach, 1, 2, types, new Random(1), null));
        }

        [TestMethod]
        public void PickWaveType_Chance_IsRoughlyHonoured_AndWeightsSteerThePick()
        {
            var rng = new Random(11);
            var types = new List<SpecialWaveDef> { Special(HoldWaveType.Breach, 3.0), Special(HoldWaveType.Runners, 1.0) };
            var special = 0;
            var breach = 0;

            for (var i = 0; i < 4000; i++)
            {
                var type = WorldEventHoldRules.PickWaveType(3, 10, HoldWaveType.Normal, 0.5, 2, types, rng, null);

                if (type != HoldWaveType.Normal)
                    special++;

                if (type == HoldWaveType.Breach)
                    breach++;
            }

            Assert.AreEqual(0.5, special / 4000.0, 0.05, "about half the waves are special");
            Assert.AreEqual(0.75, breach / (double)special, 0.05, "weight 3 to 1");
        }

        [TestMethod]
        public void TryParseWaveType_KnowsTheTypes_AndRefusesTheRest()
        {
            Assert.IsTrue(WorldEventHoldRules.TryParseWaveType("breach", out var t) && t == HoldWaveType.Breach);
            Assert.IsTrue(WorldEventHoldRules.TryParseWaveType(" Hunters ", out t) && t == HoldWaveType.Hunters);
            Assert.IsTrue(WorldEventHoldRules.TryParseWaveType("FINALE", out t) && t == HoldWaveType.Finale);
            Assert.IsFalse(WorldEventHoldRules.TryParseWaveType("normal", out _));
            Assert.IsFalse(WorldEventHoldRules.TryParseWaveType("swarm", out _));
            Assert.IsFalse(WorldEventHoldRules.TryParseWaveType("3", out _), "a number is not a type");
            Assert.IsFalse(WorldEventHoldRules.TryParseWaveType(null, out _));
        }

        [TestMethod]
        public void StartArgs_WaveType_ParsesAndRejects()
        {
            Assert.IsTrue(ACE.Server.Command.Handlers.WorldEventCommands.TryParseStartArgs(
                new[] { "--here", "--goal", "defend_envoys", "--wave-type", "breach" }, out var parsed, out var error), error);
            Assert.AreEqual(HoldWaveType.Breach, parsed.ForcedWaveType);

            Assert.IsTrue(ACE.Server.Command.Handlers.WorldEventCommands.TryParseStartArgs(
                new[] { "--here", "--wave-type", "Hunters" }, out parsed, out error), error);
            Assert.AreEqual(HoldWaveType.Hunters, parsed.ForcedWaveType);

            Assert.IsTrue(ACE.Server.Command.Handlers.WorldEventCommands.TryParseStartArgs(new[] { "--here" }, out parsed, out error), error);
            Assert.IsNull(parsed.ForcedWaveType, "absent means waves roll normally");

            foreach (var bad in new[] { "finale", "normal", "swarm", "2" })
            {
                Assert.IsFalse(ACE.Server.Command.Handlers.WorldEventCommands.TryParseStartArgs(
                    new[] { "--here", "--wave-type", bad }, out _, out error), bad);
                StringAssert.Contains(error, "--wave-type");
                AssertAscii(error);
            }
        }

        // ---- breach side, hunters, ranged, runners -----------------------------------------------------------

        [TestMethod]
        [DataRow(0.0, 10.0, "north")]
        [DataRow(10.0, 0.0, "east")]
        [DataRow(0.0, -10.0, "south")]
        [DataRow(-10.0, 0.0, "west")]
        [DataRow(7.0, 7.0, "northeast")]
        [DataRow(7.0, -7.0, "southeast")]
        [DataRow(-7.0, -7.0, "southwest")]
        [DataRow(-7.0, 7.0, "northwest")]
        [DataRow(0.1, 0.1, "center")]
        public void CompassSide_NamesTheSide(double dx, double dy, string expected)
        {
            Assert.AreEqual(expected, WorldEventHoldRules.CompassSide(dx, dy));
        }

        [TestMethod]
        public void CompassSide_MatchesTheRingLayout()
        {
            // Three targets on the ring: whatever side names they get, they are three different ones.
            var ring = WorldEventHoldRules.RingOffsets(3, 11.0);
            var sides = ring.Select(o => WorldEventHoldRules.CompassSide(o.Dx, o.Dy)).ToList();

            Assert.AreEqual(3, sides.Distinct().Count(), string.Join(",", sides));
        }

        [TestMethod]
        public void PickHunterIndices_NonSeekersFirst_ThenSeekers_NeverTheChampion()
        {
            var seekers = new HashSet<int> { 0, 1, 2, 3 };

            CollectionAssert.AreEquivalent(new[] { 4, 5 }, WorldEventHoldRules.PickHunterIndices(6, 2, seekers, -1).ToList());
            CollectionAssert.AreEquivalent(new[] { 4, 5, 0 }, WorldEventHoldRules.PickHunterIndices(6, 3, seekers, -1).ToList(),
                "a seeker is borrowed when the non-seekers run out");
            CollectionAssert.AreEquivalent(new[] { 4, 0 }, WorldEventHoldRules.PickHunterIndices(6, 2, seekers, 5).ToList(),
                "the finale champion is never a hunter");
            Assert.AreEqual(3, WorldEventHoldRules.PickHunterIndices(3, 10, null, -1).Count, "never more than the wave");
            Assert.AreEqual(0, WorldEventHoldRules.PickHunterIndices(5, 0, seekers, -1).Count);
        }

        [TestMethod]
        public void ReplaceWithRanged_SwapsEveryMonster_OrLeavesTheWaveAlone()
        {
            var wave = new List<uint> { 1, 1, 2, 2, 3 };
            var pool = new List<uint> { 90, 91 };

            var replaced = WorldEventHoldRules.ReplaceWithRanged(wave, pool, new Random(3));

            Assert.AreEqual(wave.Count, replaced.Count, "the same size");
            Assert.IsTrue(replaced.All(pool.Contains), "every one from the ranged pool");
            Assert.AreEqual(2, replaced.Distinct().Count(), "the pool is cycled, not one member repeated");

            CollectionAssert.AreEqual(wave, WorldEventHoldRules.ReplaceWithRanged(wave, new List<uint>(), new Random(3)));
        }

        [TestMethod]
        public void RangedPool_PrefersInBandMembers_ThenAnyRangedMember()
        {
            var members = new List<FamilyMember>
            {
                new FamilyMember { Wcid = 10, Level = 30, Caster = true },
                new FamilyMember { Wcid = 11, Level = 30 },
                new FamilyMember { Wcid = 12, Level = 90, Caster = true },
                new FamilyMember { Wcid = 13, Level = 32 }
            };

            bool Ranged(FamilyMember m) => m.Caster || m.Wcid == 13;

            CollectionAssert.AreEquivalent(new uint[] { 10, 13 }, WorldEventRangedMembers.Pool(members, new LevelBand(25, 40), Ranged));
            CollectionAssert.AreEquivalent(new uint[] { 10, 12, 13 }, WorldEventRangedMembers.Pool(members, new LevelBand(150, 200), Ranged),
                "no ranged member in band: any ranged member");
            Assert.AreEqual(0, WorldEventRangedMembers.Pool(members, new LevelBand(25, 40), _ => false).Count, "no ranged members at all");
        }

        [TestMethod]
        public void RangedMember_OnlyARealRangedAttackCounts()
        {
            var old = WorldEventRangedMembers.RangedLookup;

            try
            {
                WorldEventRangedMembers.ClearCache();
                WorldEventRangedMembers.RangedLookup = wcid => wcid == 777001;

                Assert.IsTrue(WorldEventRangedMembers.IsRanged(new FamilyMember { Wcid = 777001 }), "an archer with arrows, or a war caster");
                Assert.IsFalse(WorldEventRangedMembers.IsRanged(new FamilyMember { Wcid = 777002 }), "a brawler");
                Assert.IsFalse(WorldEventRangedMembers.IsRanged(new FamilyMember { Wcid = 777000, Caster = true }),
                    "the in-game bug: a spellbook alone (buffs, heals) no longer makes a melee monster ranged");
                Assert.IsFalse(WorldEventRangedMembers.IsRanged(null));
            }
            finally
            {
                WorldEventRangedMembers.RangedLookup = old;
                WorldEventRangedMembers.ClearCache();
            }
        }

        [TestMethod]
        public void RealRangedAttack_TheRule()
        {
            Assert.IsTrue(WorldEventRangedMembers.HasRealRangedAttack(launcherWielded: true, hasMatchingAmmo: true, thrownWielded: false, offensiveCastChance: 0), "a bow and arrows");
            Assert.IsFalse(WorldEventRangedMembers.HasRealRangedAttack(true, false, false, 0), "a bow with no arrows (or the wrong ammo)");
            Assert.IsTrue(WorldEventRangedMembers.HasRealRangedAttack(false, false, true, 0), "throwing axes");
            Assert.IsTrue(WorldEventRangedMembers.HasRealRangedAttack(false, false, false, 0.25), "a 25% Harm: a caster");
            Assert.IsFalse(WorldEventRangedMembers.HasRealRangedAttack(false, false, false, 0.06), "two 3% bolts on a melee monster (Grievver Shredder): not a caster");
            Assert.IsTrue(WorldEventRangedMembers.HasRealRangedAttack(false, false, false, WorldEventRangedMembers.MinOffensiveCastChance), "exactly the bar");
            Assert.IsFalse(WorldEventRangedMembers.HasRealRangedAttack(false, true, false, 0), "arrows and no bow");
            Assert.IsFalse(WorldEventRangedMembers.HasRealRangedAttack(false, false, false, 0), "nothing at range");

            // Spells: projectiles and Harm are offensive; buffs and heals are not.
            Assert.IsTrue(WorldEventRangedMembers.IsOffensiveSpell(projectileWcid: 1499, numProjectiles: 1, boost: null, vitalDamageType: null), "a war bolt");
            Assert.IsTrue(WorldEventRangedMembers.IsOffensiveSpell(null, null, -50, (int)ACE.Entity.Enum.DamageType.Health), "Harm Other");
            Assert.IsFalse(WorldEventRangedMembers.IsOffensiveSpell(null, null, 50, (int)ACE.Entity.Enum.DamageType.Health), "Heal Self");
            Assert.IsFalse(WorldEventRangedMembers.IsOffensiveSpell(null, null, -50, (int)ACE.Entity.Enum.DamageType.Stamina), "a stamina drain is not a health attack");
            Assert.IsFalse(WorldEventRangedMembers.IsOffensiveSpell(null, null, null, null), "a buff");
        }

        [TestMethod]
        public void RunnersKeep_TrimsTheWave_ButKeepsOne()
        {
            Assert.AreEqual(6, WorldEventHoldRules.RunnersKeep(10, 0.6));
            Assert.AreEqual(1, WorldEventHoldRules.RunnersKeep(1, 0.6));
            Assert.AreEqual(1, WorldEventHoldRules.RunnersKeep(2, 0.1));
            Assert.AreEqual(10, WorldEventHoldRules.RunnersKeep(10, 1.0), "1 keeps them all");
            Assert.AreEqual(0, WorldEventHoldRules.RunnersKeep(0, 0.6));
        }

        [TestMethod]
        public void PickHealthiest_ByShareOfMaxHealth()
        {
            Assert.AreEqual(1, WorldEventHoldRules.PickHealthiest(new List<(uint, uint)> { (5000, 10000), (9000, 10000), (8000, 10000) }));
            Assert.AreEqual(0, WorldEventHoldRules.PickHealthiest(new List<(uint, uint)> { (100, 100), (9000, 10000) }), "a share, not a raw number");
            Assert.AreEqual(-1, WorldEventHoldRules.PickHealthiest(new List<(uint, uint)>()));
        }

        // ---- the run monster flags --------------------------------------------------------------------------

        [TestMethod]
        public void HoldFlags_DefaultOff_ForEveryCreature()
        {
            var creature = BuildHoldCreature();

            Assert.AreEqual(0u, creature.WorldEventSeekForcedGuid);
            Assert.IsFalse(creature.WorldEventSeekHealthiest);
            Assert.IsFalse(creature.WorldEventHunter);
            Assert.AreEqual(1.0f, creature.WorldEventRunRateMult);
        }

        [TestMethod]
        public void HoldWavePlan_LogText_NamesTheType()
        {
            Assert.AreEqual("type=normal", new HoldWavePlan().ToString());
            StringAssert.Contains(new HoldWavePlan { Type = HoldWaveType.Breach, BreachTargetGuid = 0x80000001, BreachSide = "east" }.ToString(), "side=east");
            StringAssert.Contains(new HoldWavePlan { Type = HoldWaveType.Finale }.ToString(), "champion=none");
        }

        // ---- heal reduction after a hit ---------------------------------------------------------------------

        [TestMethod]
        public void HealAfterHitFactor_ReducesInsideTheWindowOnly()
        {
            Assert.AreEqual(0.5, WorldEventHoldRules.HealAfterHitFactor(1.0, 0.5, 5), 1e-9);
            Assert.AreEqual(0.5, WorldEventHoldRules.HealAfterHitFactor(4.9, 0.5, 5), 1e-9);
            Assert.AreEqual(1.0, WorldEventHoldRules.HealAfterHitFactor(5.0, 0.5, 5), 1e-9, "the window has ended");
            Assert.AreEqual(1.0, WorldEventHoldRules.HealAfterHitFactor(null, 0.5, 5), 1e-9, "never hit");
            Assert.AreEqual(1.0, WorldEventHoldRules.HealAfterHitFactor(1.0, 0, 5), 1e-9, "turned off");
            Assert.AreEqual(0.0, WorldEventHoldRules.HealAfterHitFactor(1.0, 1.5, 5), 1e-9, "never below zero");
        }

        [TestMethod]
        public void ProtectedHeal_RightAfterAHit_IsReduced()
        {
            var (_, ward, monster) = BuildHitScene(120);
            var protect = ward.P_WorldEvent.Composition.Goal.Protect;
            protect.MaxHealPerTenSeconds = 0.5;
            protect.HealReductionAfterHit = 0.5;
            protect.HealReductionSeconds = 5;

            var (_, fresh, _) = BuildHitScene(120);
            fresh.P_WorldEvent.Composition.Goal.Protect.MaxHealPerTenSeconds = 0.5;

            var full = fresh.ConvertWorldEventProtectedHeal(100, T0 + 1);

            ward.ScaleWorldEventProtectedHit(monster, 12f, T0);

            var reduced = ward.ConvertWorldEventProtectedHeal(100, T0 + 1);
            var later = ward.ConvertWorldEventProtectedHeal(100, T0 + 30);

            Assert.AreEqual(full / 2.0, reduced, 1.0, "half the heal one second after a hit");
            Assert.AreEqual(full, later, "full again once the window has passed");
        }

        [TestMethod]
        public void ProtectedHeal_NoReductionSetting_HealsInFull()
        {
            var (_, ward, monster) = BuildHitScene(120);
            ward.P_WorldEvent.Composition.Goal.Protect.MaxHealPerTenSeconds = 0.5;

            ward.ScaleWorldEventProtectedHit(monster, 12f, T0);

            Assert.AreEqual((int)Math.Floor(ward.Health.MaxValue * 0.04), ward.ConvertWorldEventProtectedHeal(100, T0 + 1));
        }

        // ---- target layout by crowd -------------------------------------------------------------------------

        [TestMethod]
        public void ResolveLayout_SmallGroupsGetFewerTargets()
        {
            var protect = BuildProtect(countBase: 3, countCap: 3);
            protect.Radius = 11.0;
            protect.CountByParticipants = new List<TargetLayoutDef>
            {
                new TargetLayoutDef { MinPlayers = 4, Count = 3, Radius = 11.0 },
                new TargetLayoutDef { MinPlayers = 0, Count = 2, Radius = 8.0 }
            };

            Assert.AreEqual((2, 8.0), protect.ResolveLayout(0), "a solo staff test counts nobody");
            Assert.AreEqual((2, 8.0), protect.ResolveLayout(1));
            Assert.AreEqual((2, 8.0), protect.ResolveLayout(3));
            Assert.AreEqual((3, 11.0), protect.ResolveLayout(4));
            Assert.AreEqual((3, 11.0), protect.ResolveLayout(40));

            protect.CountByParticipants = null;
            Assert.AreEqual((3, 11.0), protect.ResolveLayout(1), "without the list, count and radius as before");

            protect.CountByParticipants = new List<TargetLayoutDef> { new TargetLayoutDef { MinPlayers = 5, Count = 2, Radius = 8.0 } };
            Assert.AreEqual((3, 11.0), protect.ResolveLayout(2), "below every entry: count and radius as before");
        }

        // ---- goals.json ----------------------------------------------------------------------------------------

        private const string SpecialWavesJson =
            @"""waves"": { ""base"": 4, ""perParticipant"": 0.2, ""cap"": 10 }, ""minSeconds"": 300.0,
              ""healReductionAfterHit"": 0.5, ""healReductionSeconds"": 5,
              ""countByParticipants"": [ { ""minPlayers"": 0, ""count"": 2, ""radius"": 8.0 }, { ""minPlayers"": 4, ""count"": 3, ""radius"": 11.0 } ],
              ""specialWaveChance"": 0.5, ""specialWaveFromWave"": 2,
              ""specialWaves"": [
                { ""type"": ""breach"", ""announce"": [ ""One big push!"" ], ""lines"": [ ""They are massing on the {side} post!"" ] },
                { ""type"": ""runners"", ""countMult"": 0.6, ""healthMult"": 0.6, ""speedMult"": 1.5, ""announce"": [ ""Fast ones!"" ] },
                { ""type"": ""ranged"", ""announce"": [ ""Archers!"" ] },
                { ""type"": ""hunters"", ""hunters"": 2, ""announce"": [ ""Guard your healers!"" ] },
                { ""type"": ""finale"", ""announce"": [ ""{champion} leads them in!"" ] } ],";

        private static string WithSpecials(string extra) =>
            HoldGoalsJson(SampleProtectJson.Replace(@"""radius"": 6.0,", @"""radius"": 11.0, " + extra), holdSeconds: 480);

        [TestMethod]
        public void GoalsJson_SpecialWaves_ParseClean()
        {
            var store = WorldEventAxisStore.Parse(null, null, WithSpecials(SpecialWavesJson), null, null);

            Assert.IsTrue(store.Goals.ContainsKey("hold_wards"), string.Join(" | ", store.Diagnostics));
            Assert.AreEqual(0, store.Diagnostics.Count, string.Join(" | ", store.Diagnostics));

            var p = store.Goals["hold_wards"].Protect;

            Assert.AreEqual(5, p.SpecialWaves.Count);
            CollectionAssert.AreEqual(
                new[] { HoldWaveType.Breach, HoldWaveType.Runners, HoldWaveType.Ranged, HoldWaveType.Hunters, HoldWaveType.Finale },
                p.SpecialWaves.Select(w => w.TypeKind).ToArray(), "types parsed at load");
            Assert.IsNotNull(p.Finale);
            Assert.AreEqual(2, p.SpecialWave(HoldWaveType.Hunters).Hunters);
            Assert.AreEqual(1.5, p.SpecialWave(HoldWaveType.Runners).SpeedMult, 1e-9);
            Assert.AreEqual(0.5, p.SpecialWaveChance, 1e-9);
            Assert.AreEqual(0.5, p.HealReductionAfterHit, 1e-9);
            Assert.AreEqual(2, p.CountByParticipants.Count);
        }

        [TestMethod]
        public void GoalsJson_DefaultsLeaveEverythingOff()
        {
            var p = new ProtectDef();

            Assert.AreEqual(0, p.SpecialWaveChance, "no special waves unless asked for");
            Assert.AreEqual(2, p.SpecialWaveFromWave);
            Assert.AreEqual(0, p.HealReductionAfterHit, "no heal reduction unless asked for");
            Assert.AreEqual(5, p.HealReductionSeconds, 1e-9);
            Assert.IsNull(p.SpecialWaves);
            Assert.IsNull(p.CountByParticipants);
            Assert.IsNull(p.Finale);
        }

        [TestMethod]
        public void GoalsJson_BadSpecialWaveEntries_AreDropped_NotTheGoal()
        {
            var json = SpecialWavesJson
                .Replace(@"{ ""type"": ""ranged"", ""announce"": [ ""Archers!"" ] },",
                    @"{ ""type"": ""ranged"", ""announce"": [ ""Archers!"" ] }, { ""type"": ""swarm"" }, { ""type"": ""breach"" },
                      { ""type"": ""runners"", ""speedMult"": 9 },")
                .Replace(@"{ ""minPlayers"": 4, ""count"": 3, ""radius"": 11.0 }", @"{ ""minPlayers"": 4, ""count"": 9, ""radius"": 11.0 }");

            var store = WorldEventAxisStore.Parse(null, null, WithSpecials(json), null, null);

            Assert.IsTrue(store.Goals.ContainsKey("hold_wards"), string.Join(" | ", store.Diagnostics));

            var p = store.Goals["hold_wards"].Protect;

            Assert.AreEqual(5, p.SpecialWaves.Count, "the five good entries stay");
            Assert.IsTrue(store.Diagnostics.Any(d => d.Contains("'swarm'") && d.Contains("unknown type")));
            Assert.IsTrue(store.Diagnostics.Any(d => d.Contains("'breach'") && d.Contains("repeats a type")));
            Assert.IsTrue(store.Diagnostics.Any(d => d.Contains("'runners'") && d.Contains("speedMult")));
            Assert.AreEqual(1, p.CountByParticipants.Count, "the 9-target layout is dropped");
            Assert.IsTrue(store.Diagnostics.Any(d => d.Contains("countByParticipants")));
        }

        [TestMethod]
        public void GoalsJson_SpecialWaveLines_DashesRepaired()
        {
            var json = SpecialWavesJson.Replace("Fast ones!", "Fast ones " + (char)0x2014 + " stop them!");

            var store = WorldEventAxisStore.Parse(null, null, WithSpecials(json), null, null);
            var runners = store.Goals["hold_wards"].Protect.SpecialWave(HoldWaveType.Runners);

            Assert.AreEqual("Fast ones - stop them!", runners.Announce[0]);
            Assert.IsTrue(store.Diagnostics.Any(d => d.Contains("specialWaves.runners.announce") && d.Contains("dash")));
        }

        [TestMethod]
        public void GoalsJson_SpecialWavesWithoutWaves_AreIgnored()
        {
            var json = SpecialWavesJson.Replace(@"""waves"": { ""base"": 4, ""perParticipant"": 0.2, ""cap"": 10 }, ""minSeconds"": 300.0,", "");

            var store = WorldEventAxisStore.Parse(null, null, WithSpecials(json), null, null);

            Assert.IsTrue(store.Goals.ContainsKey("hold_wards"), string.Join(" | ", store.Diagnostics));
            Assert.IsNull(store.Goals["hold_wards"].Protect.SpecialWaves);
            Assert.IsTrue(store.Diagnostics.Any(d => d.Contains("specialWaves needs protect.waves")));
        }

        [TestMethod]
        [DataRow(@"""specialWaveChance"": 1.5,", "specialWaveChance")]
        [DataRow(@"""specialWaveFromWave"": 0,", "specialWaveFromWave")]
        [DataRow(@"""healReductionAfterHit"": -0.1,", "healReductionAfterHit")]
        [DataRow(@"""healReductionSeconds"": 600,", "healReductionSeconds")]
        public void GoalsJson_BadDesignPassSetting_DropsTheGoal(string field, string expected)
        {
            var json = WithSpecials(field + " " + SpecialWavesJson
                .Replace(@"""specialWaveChance"": 0.5, ""specialWaveFromWave"": 2,", "")
                .Replace(@"""healReductionAfterHit"": 0.5, ""healReductionSeconds"": 5,", ""));

            var store = WorldEventAxisStore.Parse(null, null, json, null, null);

            Assert.IsFalse(store.Goals.ContainsKey("hold_wards"));
            Assert.IsTrue(store.Diagnostics.Any(d => d.Contains(expected)), string.Join(" | ", store.Diagnostics));
        }

        // ---- shipped lines ------------------------------------------------------------------------------------

        private static int SentenceCount(string line) => Regex.Matches(line.Trim(), @"[.!?]+(\s|$)").Count;

        [TestMethod]
        public void SpecialWaveLines_Ascii_ShortAndFilled()
        {
            foreach (var family in SampleFamilies)
            {
                foreach (var template in EnvoySpecialWaveLines)
                {
                    var line = WorldEventHoldRules.FillLine(WorldEventHoldRules.FillWaveTokens(template, "east", "Champion Drudge Ravener"),
                        family, "envoy", "envoys", "Holtburg", 3, 3);

                    AssertAscii(line);
                    Assert.IsFalse(line.Contains('{') || line.Contains('}'), $"leftover token: {line}");
                    Assert.IsTrue(SentenceCount(line) <= 3, $"more than three sentences: {line}");
                    Assert.IsFalse(line.IndexOf("warp", StringComparison.OrdinalIgnoreCase) >= 0, $"names the Warp: {line}");
                }
            }
        }

        [TestMethod]
        public void FillWaveTokens_FallsBackWhenUnknown()
        {
            Assert.AreEqual("They are massing on the east post!", WorldEventHoldRules.FillWaveTokens("They are massing on the {side} post!", "east", null));
            Assert.AreEqual("They are massing on the nearest post!", WorldEventHoldRules.FillWaveTokens("They are massing on the {side} post!", null, null));
            Assert.AreEqual("Their champion leads them in!", WorldEventHoldRules.FillWaveTokens("{champion} leads them in!", null, null));
        }

        private static string FindContentFile(string relative)
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, relative);

                if (File.Exists(candidate))
                    return candidate;
            }

            return null;
        }
    }
}
