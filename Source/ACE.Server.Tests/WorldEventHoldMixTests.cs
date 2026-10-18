using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Defs;
using ACE.Server.WorldEvents.Objectives;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The 2026-10-18 evening round: the health bar (protect.showHealthBar), mixed waves (protect.waveMix) and the
    /// finale boss that arrives alone, bigger and named. Shares the builders in WorldEventHoldTests.
    /// </summary>
    public partial class WorldEventHoldTests
    {
        // ---- health bar: shown to the client, still refused by the server -----------------------------------

        private static ObjectDescriptionFlag ClientDescriptionFlags(WorldObject wo)
        {
            // The private pass the create packet runs before it writes the flags.
            typeof(WorldObject).GetMethod("UpdateObjectDescriptionFlags", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(wo, null);

            return wo.ObjectDescriptionFlags;
        }

        private static Creature BuildShownTarget(WorldEvent evt, bool showHealthBar)
        {
            var target = BuildHoldCreature(ProtectDef.TimeScaledMaxHealth);
            WorldEventSpawner.ApplyProtectedAppearance(target);
            target.WorldEventProtected = true;
            target.WorldEventShowHealthBar = showHealthBar;
            target.P_WorldEvent = evt;
            return target;
        }

        [TestMethod]
        public void ShowHealthBar_ClientSeesAttackable_ServerKeepsItNonAttackable()
        {
            var (evt, _) = BuildTimeScaledRun();

            var shown = BuildShownTarget(evt, showHealthBar: true);
            Assert.IsFalse(shown.Attackable, "server-side the target stays non-attackable");
            Assert.IsTrue(shown.ClientAttackable);
            Assert.IsTrue(ClientDescriptionFlags(shown).HasFlag(ObjectDescriptionFlag.Attackable), "the client is told attackable: it draws the bar");

            var friendly = BuildShownTarget(evt, showHealthBar: false);
            Assert.IsFalse(friendly.ClientAttackable);
            Assert.IsFalse(ClientDescriptionFlags(friendly).HasFlag(ObjectDescriptionFlag.Attackable), "showHealthBar false: the friendly yellow name, no bar");

            // Nothing else changes: an ordinary monster and an ordinary non-attackable NPC are sent as before.
            var monster = BuildHoldCreature();
            Assert.AreEqual(monster.Attackable, monster.ClientAttackable);

            var npc = BuildHoldCreature();
            npc.Attackable = false;
            npc.WorldEventShowHealthBar = true;   // without WorldEventProtected the flag means nothing
            Assert.IsFalse(npc.ClientAttackable);
        }

        [TestMethod]
        public void ShowHealthBar_PlayersAndPetsAndOtherRunsStillCannotDamage()
        {
            var (evt, _) = BuildTimeScaledRun();
            var otherRun = BuildTimeScaledRun().Evt;

            foreach (var showBar in new[] { true, false })
            {
                var target = BuildShownTarget(evt, showBar);

                // A player (Player.CanDamage reads only the target).
                var player = (Player)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Player));
                Assert.IsFalse(player.CanDamage(target), $"showHealthBar={showBar}: a player can never damage it");

                // Even if something flipped the server-side flag, the protected check still refuses.
                target.Attackable = true;
                Assert.IsFalse(player.CanDamage(target), $"showHealthBar={showBar}: refused even with Attackable set");
                target.Attackable = false;

                // A monster from another run, and an ordinary monster.
                var stranger = BuildHoldCreature();
                stranger.P_WorldEvent = otherRun;
                Assert.IsFalse(stranger.CanDamage(target));
                Assert.IsFalse(BuildHoldCreature().CanDamage(target));

                // Its own run's monster: yes.
                var runMonster = BuildHoldCreature();
                runMonster.P_WorldEvent = evt;
                Assert.IsTrue(runMonster.CanDamage(target));

                // A player's hit through the damage pipeline is never a protected hit.
                Assert.IsFalse(DamageEvent.IsWorldEventProtectedHit(player, target));
            }
        }

        // ---- the wave mix -----------------------------------------------------------------------------------

        /// <summary>The default split, in role order Seeker, Breach, Runner, Ranged, Hunter, Corrupted.</summary>
        private static readonly double[] DefaultMix = new WaveMixDef().Shares;

        private static readonly WaveMixDef DefaultMixDef = new WaveMixDef();

        private static int[] DefaultCounts(int size) =>
            WorldEventHoldRules.WaveMixCounts(size, DefaultMix, DefaultMixDef.Minimums, DefaultMixDef.Maximums);

        [TestMethod]
        public void WaveMix_DefaultSplit_EveryRoleInEveryWave()
        {
            CollectionAssert.AreEqual(new[] { 0.2, 0.15, 0.15, 0.3, 0.1, 0.1 }, DefaultMix);
            Assert.AreEqual(3, DefaultMixDef.MinRanged);
            Assert.AreEqual(1, DefaultMixDef.MinCorrupted);
            Assert.AreEqual(2, DefaultMixDef.MaxCorrupted);

            // Solo staff wave of 11: 2 seekers, 2 breach, 2 runners, 3 ranged, 1 hunter, 1 corrupted.
            CollectionAssert.AreEqual(new[] { 2, 2, 2, 3, 1, 1 }, DefaultCounts(11));

            // One counted player (13): 3 / 2 / 2 / 4 / 1 / 1.
            CollectionAssert.AreEqual(new[] { 3, 2, 2, 4, 1, 1 }, DefaultCounts(13));

            foreach (var size in new[] { 8, 11, 13, 19, 27, 40 })
            {
                var counts = DefaultCounts(size);

                Assert.AreEqual(size, counts.Sum(), $"wave of {size}: every monster has a role");
                Assert.IsTrue(counts.All(c => c >= 1), $"wave of {size}: every role is there ({string.Join(",", counts)})");
                Assert.IsTrue(counts[(int)HoldRole.Ranged] >= 3, $"wave of {size}: at least 3 ranged");
                Assert.IsTrue(counts[(int)HoldRole.Ranged] >= Math.Floor(size * 0.3), $"wave of {size}: about 30% ranged");
                Assert.IsTrue(counts[(int)HoldRole.Corrupted] >= 1 && counts[(int)HoldRole.Corrupted] <= 2, $"wave of {size}: 1 or 2 corrupted");

                foreach (var r in new[] { HoldRole.Breach, HoldRole.Runner, HoldRole.Hunter })
                    Assert.AreEqual(DefaultMix[(int)r] * size, counts[(int)r], 1.5, $"wave of {size}: role {r} scales with the wave");
            }

            Assert.AreEqual(2, DefaultCounts(40)[(int)HoldRole.Corrupted], "40 monsters: corrupted capped at 2");
            Assert.AreEqual(1, DefaultCounts(11)[(int)HoldRole.Corrupted]);
            Assert.AreEqual(2, DefaultCounts(19)[(int)HoldRole.Corrupted], "19 monsters: 2 corrupted");

            // Too small for every minimum: still sums, nothing invented.
            Assert.AreEqual(3, DefaultCounts(3).Sum());
            Assert.AreEqual(5, DefaultCounts(5).Sum());

            // All shares zero: the whole wave seeks.
            CollectionAssert.AreEqual(new[] { 6, 0, 0, 0, 0, 0 }, WorldEventHoldRules.WaveMixCounts(6, new double[6]));

            // A role switched off gets nobody, and its minimum is ignored.
            var noRanged = new WaveMixDef { Ranged = 0 };
            Assert.AreEqual(0, WorldEventHoldRules.WaveMixCounts(11, noRanged.Shares, noRanged.Minimums, noRanged.Maximums)[(int)HoldRole.Ranged]);
            Assert.AreEqual(0, WorldEventHoldRules.WaveMixCounts(11, new[] { 0.5, 0.5, 0, 0, 0, 0 })[(int)HoldRole.Runner]);
        }

        [TestMethod]
        public void WaveMix_Limits_MinimumsTakeFromTheBiggestRoles_MaximumsGoToSeekers()
        {
            // 10% corrupted of 40 is 4: capped at 2, the 2 extra seek.
            var free = WorldEventHoldRules.WaveMixCounts(40, DefaultMix);
            var limited = DefaultCounts(40);

            Assert.AreEqual(4, free[(int)HoldRole.Corrupted]);
            Assert.AreEqual(2, limited[(int)HoldRole.Corrupted]);
            Assert.AreEqual(free[(int)HoldRole.Seeker] + 2, limited[(int)HoldRole.Seeker]);

            // A minimum of 3 ranged in a wave of 8 (30% would be 2): taken from the biggest other role.
            Assert.AreEqual(3, DefaultCounts(8)[(int)HoldRole.Ranged]);

            // A minimum never takes another role below 1.
            Assert.IsTrue(DefaultCounts(8).All(c => c >= 1));
        }

        [TestMethod]
        public void WaveMix_Assign_MatchesTheCounts_AndKeepsElitesOffRanged()
        {
            for (var seed = 0; seed < 50; seed++)
            {
                var keep = new HashSet<int> { 2, 10 };
                var roles = WorldEventHoldRules.AssignWaveMix(11, DefaultMix, DefaultMixDef.Minimums, DefaultMixDef.Maximums, keep, new Random(seed));

                Assert.AreEqual(11, roles.Length);
                Assert.AreEqual(2, roles.Count(r => r == HoldRole.Breach));
                Assert.AreEqual(3, roles.Count(r => r == HoldRole.Ranged));
                Assert.AreEqual(1, roles.Count(r => r == HoldRole.Corrupted));
                Assert.AreNotEqual(HoldRole.Ranged, roles[2], "an elite keeps its own monster");
                Assert.AreNotEqual(HoldRole.Ranged, roles[10], "an overflow champion keeps its own monster");
            }

            var trash = new List<uint> { 1, 1, 1, 1 };
            var ranged = WorldEventHoldRules.ReplaceRangedSlots(trash, new[] { HoldRole.Seeker, HoldRole.Ranged, HoldRole.Hunter, HoldRole.Ranged }, new uint[] { 9 }, new Random(1));
            CollectionAssert.AreEqual(new uint[] { 1, 9, 1, 9 }, ranged, "only the Ranged slots are redrawn");
            CollectionAssert.AreEqual(trash, WorldEventHoldRules.ReplaceRangedSlots(trash, new[] { HoldRole.Ranged }, new uint[0], new Random(1)),
                "no ranged members: the slot keeps its monster");
        }


        [TestMethod]
        public void WaveMix_Roles_SetTheRightFlags()
        {
            var plan = new HoldWavePlan { Type = HoldWaveType.Mixed, BreachTargetGuid = 0x80000364, RunRateMult = 1.5f, AttackSpeedMult = 1.5f, HoldDistance = 18 };

            Creature Role(HoldRole role)
            {
                var c = BuildHoldCreature();
                WorldEventSpawner.ApplyHoldRole(c, role, plan);
                return c;
            }

            var breach = Role(HoldRole.Breach);
            Assert.IsTrue(breach.WorldEventSeeker);
            Assert.AreEqual(0x80000364u, breach.WorldEventSeekForcedGuid, "the breach squad all goes for one target");

            var runner = Role(HoldRole.Runner);
            Assert.IsTrue(runner.WorldEventSeeker, "runners always go for the targets");
            Assert.IsFalse(runner.WorldEventHunter);
            Assert.AreEqual(1.5f, runner.WorldEventRunRateMult);
            Assert.AreEqual(1.5f, runner.WorldEventAttackSpeedMult);

            var ranged = Role(HoldRole.Ranged);
            Assert.IsTrue(ranged.WorldEventSeeker, "ranged attack the targets");
            Assert.AreEqual(18f, ranged.WorldEventHoldDistance);

            var hunter = Role(HoldRole.Hunter);
            Assert.IsFalse(hunter.WorldEventSeeker, "hunters go for players");
            Assert.IsTrue(hunter.WorldEventHunter);

            var seeker = Role(HoldRole.Seeker);
            Assert.IsTrue(seeker.WorldEventSeeker);
            Assert.AreEqual(0u, seeker.WorldEventSeekForcedGuid);

            foreach (var spread in new[] { seeker, runner, ranged })
                Assert.AreEqual(0x80000364u, spread.WorldEventSeekAvoidGuid, "seekers, runners and ranged spread over the other targets");
        }

        [TestMethod]
        public void SeekAvoid_TheBreachTargetOnlyWhenNothingElseStands()
        {
            var guids = new uint[] { 0xA, 0xB, 0xC };
            var dist = new List<double> { 100, 400, 900 };

            // Without avoiding, the nearest of the least-attacked: A.
            Assert.AreEqual(0, WorldEventHoldRules.PickLeastAttacked(new List<int> { 0, 0, 0 }, dist, 10000));

            // Avoiding A (the breach target): B, even with more attackers on it than A has.
            var counts = WorldEventHoldRules.AvoidTarget(new List<int> { 0, 3, 3 }, guids, 0xA);
            Assert.AreEqual(1, WorldEventHoldRules.PickLeastAttacked(counts, dist, 10000));

            // Only A left: A.
            var alone = WorldEventHoldRules.AvoidTarget(new List<int> { 5 }, new uint[] { 0xA }, 0xA);
            Assert.AreEqual(0, WorldEventHoldRules.PickLeastAttacked(alone, new List<double> { 100 }, 10000));

            CollectionAssert.AreEqual(new List<int> { 1, 2 }, WorldEventHoldRules.AvoidTarget(new List<int> { 1, 2 }, new uint[] { 1, 2 }, 0), "0: nothing avoided");
        }

        [TestMethod]
        public void WaveMix_GoalsJson_Validates()
        {
            void Refused(string with, string expected)
            {
                var json = ShippedCaravanProtectJson.Replace(@"""radius"": 6.0,", @"""radius"": 6.0, ""waves"": { ""base"": 6, ""perParticipant"": 0, ""cap"": 6 }, ""minSeconds"": 0, " + with);
                var store = WorldEventAxisStore.Parse(null, null, HoldGoalsJson(json, holdSeconds: 480), null, null);

                Assert.IsFalse(store.Goals.ContainsKey("hold_wards"), with);
                Assert.IsTrue(store.Diagnostics.Any(d => d.Contains(expected)), string.Join(" | ", store.Diagnostics));
            }

            Refused(@"""waveMix"": { ""breach"": 2 },", "protect.waveMix");
            Refused(@"""waveMix"": { ""breach"": 0, ""runners"": 0, ""ranged"": 0, ""hunters"": 0, ""seekers"": 0, ""corrupted"": 0 },", "protect.waveMix");
            Refused(@"""finaleBossPrefix"": ""Warlord!"",", "protect.finaleBossPrefix");
            Refused(@"""finaleBossLeadSeconds"": 45,", "protect.finaleBossLeadSeconds");
            Refused(@"""bossArrivalEffect"": ""Fireworks"",", "protect.bossArrivalEffect");
            Refused(@"""finaleBossScale"": 3,", "protect.finaleBossScale");

            var ok = ShippedCaravanProtectJson.Replace(@"""radius"": 6.0,",
                @"""radius"": 6.0, ""waves"": { ""base"": 6, ""perParticipant"": 0, ""cap"": 6 }, ""minSeconds"": 0, ""waveMix"": { ""announce"": [ ""Watch the {side} side!"" ] }, ""showHealthBar"": false,");
            var good = WorldEventAxisStore.Parse(null, null, HoldGoalsJson(ok, holdSeconds: 480), null, null);

            Assert.IsTrue(good.Goals.ContainsKey("hold_wards"), string.Join(" | ", good.Diagnostics));
            Assert.IsFalse(good.Goals["hold_wards"].Protect.ShowHealthBar);
            CollectionAssert.AreEqual(DefaultMix, good.Goals["hold_wards"].Protect.WaveMix.Shares, "omitted shares take the default split");
        }

        [TestMethod]
        public void WaveTokens_FillTheBreachTargetByName()
        {
            Assert.AreEqual("Their main thrust is aimed at Sir Donovan on the east flank!",
                WorldEventHoldRules.FillWaveTokens("Their main thrust is aimed at {target} on the {side} flank!", "east", null, "Sir Donovan"));
            Assert.AreEqual("They go for one of us!", WorldEventHoldRules.FillWaveTokens("They go for {target}!", null, null, null));
        }

        // ---- the finale boss --------------------------------------------------------------------------------

        [TestMethod]
        public void FinaleBoss_Name_AlwaysHasTheTitle()
        {
            Assert.AreEqual("Warlord Hea Apostate Shock Trooper", WorldEventHoldRules.FinaleBossName("Warlord", "Champion Hea Apostate Shock Trooper"),
                "the promotion's Champion is replaced, not stacked");
            Assert.AreEqual("Warlord Olthoi Hive Piercer", WorldEventHoldRules.FinaleBossName("Warlord", "Olthoi Hive Piercer"));
            Assert.AreEqual("Warlord Olthoi Hive Piercer", WorldEventHoldRules.FinaleBossName("Warlord", "Warlord Olthoi Hive Piercer"));
            Assert.AreEqual("Olthoi Hive Piercer", WorldEventHoldRules.FinaleBossName("", "Elite Olthoi Hive Piercer"));
            Assert.AreEqual("Warlord", WorldEventHoldRules.FinaleBossName("Warlord", null));

            Assert.IsTrue(WorldEventHoldRules.IsValidBossPrefix("Warlord"));
            Assert.IsTrue(WorldEventHoldRules.IsValidBossPrefix("Dread Champion"));
            Assert.IsTrue(WorldEventHoldRules.IsValidBossPrefix(""));
            Assert.IsFalse(WorldEventHoldRules.IsValidBossPrefix("Warlord" + (char)0x2014), "no dashes");
            Assert.IsFalse(WorldEventHoldRules.IsValidBossPrefix("An Extremely Long Title"));
        }

        [TestMethod]
        public void FinaleBoss_IsTheOnlyOneOfItsKind()
        {
            var trash = new List<uint> { 40495, 7487, 40495, 10807, 40495 };
            var escort = WorldEventHoldRules.WithoutBossLookalikes(trash, 40495, new Random(4));

            Assert.AreEqual(5, escort.Count, "same size");
            Assert.IsFalse(escort.Contains(40495u), "no copies of the boss's monster in the rest of the final wave");

            CollectionAssert.AreEqual(new List<uint> { 40495, 40495 }, WorldEventHoldRules.WithoutBossLookalikes(new List<uint> { 40495, 40495 }, 40495, new Random(1)),
                "nothing else to swap in: unchanged");
        }

        [TestMethod]
        public void FinaleBoss_ArrivesAlone_TheRunCannotBeWonUntilTheRestLands()
        {
            // The boss batch has landed and resolved; the rest of the wave is still waiting out the lead time.
            Assert.AreEqual(4, WorldEvent.HoldResolvedWaves(5, 5, T0 + 2, T0, 15, finaleEscortPending: true));
            Assert.AreEqual(5, WorldEvent.HoldResolvedWaves(5, 6, T0 + 8, T0, 15, finaleEscortPending: false));
            Assert.AreEqual(4, WorldEvent.HoldResolvedWaves(5, 5, T0 + 60, T0, 15, finaleEscortPending: true), "the grace never skips a pending escort");

            // Killing the boss before the rest arrives is not a win.
            Assert.IsFalse(HoldObjective.WavesWon(new HoldWaveCounts(5, 5, 5, 0, 4), 5));
            Assert.IsTrue(HoldObjective.WavesWon(new HoldWaveCounts(5, 5, 5, 0, 5), 5));
        }

        [TestMethod]
        public void FinaleBoss_Defaults_AreBigAndLoud()
        {
            var p = new ProtectDef();

            Assert.AreEqual(1.75, p.FinaleBossScale, 1e-9);
            Assert.AreEqual(3.0, p.FinaleBossHealthMult, 1e-9);
            Assert.AreEqual("Warlord", p.FinaleBossPrefix);
            Assert.AreEqual(5, p.FinaleBossLeadSeconds, 1e-9);
            Assert.AreEqual(3, p.BossEffectSeconds, 1e-9);
            Assert.IsTrue(WorldEventHoldRules.TryParseBossEffect(p.BossArrivalEffect, out var arrival));
            Assert.AreEqual(PlayScript.LevelUp, arrival);
            Assert.IsTrue(p.ShowHealthBar, "health bars on by default");
        }

        // ---- the plan WorldEvent builds ----------------------------------------------------------------------

        private static (WorldEvent Evt, HoldObjective Objective) MixedRun(HoldWaveType? forced = null)
        {
            var goal = WaveHoldGoal(wavesBase: 6, wavesPerParticipant: 0, wavesCap: 6, minSeconds: 0);
            goal.Protect.WaveMix = new WaveMixDef { Announce = new List<string> { "Their main thrust is aimed at {target} on the {side} flank!" } };
            goal.Protect.SpecialWaves = new List<SpecialWaveDef>
            {
                new SpecialWaveDef { Type = "breach", TypeKind = HoldWaveType.Breach, Lines = new List<string> { "They are massing on the {side} flank!" } },
                new SpecialWaveDef { Type = "runners", TypeKind = HoldWaveType.Runners, SpeedMult = 1.6, AttackSpeedMult = 1.4, HealthMult = 0.5 },
                new SpecialWaveDef { Type = "ranged", TypeKind = HoldWaveType.Ranged, HoldDistance = 17 },
                new SpecialWaveDef { Type = "finale", TypeKind = HoldWaveType.Finale }
            };

            var evt = BuildHoldEvent(goal, null);
            evt.Request.ForcedWaveType = forced;

            return (evt, SpawnerWiredObjective(evt, goal));
        }

        private static WavePick ElevenTusks() => new WavePick(Enumerable.Repeat(1u, 11).ToList(), new List<uint>());

        [TestMethod]
        public void MixedPlan_EveryWaveButTheFinale_CarriesEveryRole()
        {
            var (evt, objective) = MixedRun();

            var target = BuildHoldCreature();
            target.Name = "Sir Donovan";
            evt.Spawner.AdoptProtectedForTest(target);
            evt.Spawner.AdoptProtectedForTest(BuildHoldCreature());

            for (var wave = 1; wave < objective.TargetWaves; wave++)
            {
                var plan = evt.BuildHoldWavePlan(objective, wave, ElevenTusks());

                Assert.AreEqual(HoldWaveType.Mixed, plan?.Type, $"wave {wave} is mixed");
                Assert.AreEqual(11, plan.Roles.Length);

                foreach (HoldRole role in Enum.GetValues(typeof(HoldRole)))
                    Assert.IsTrue(plan.Roles.Contains(role), $"wave {wave} has {role}");

                Assert.AreNotEqual(0u, plan.BreachTargetGuid);
                Assert.IsFalse(string.IsNullOrEmpty(plan.BreachSide));
                Assert.AreEqual(1.6f, plan.RunRateMult, 1e-6, "runner settings come from the goal's runners entry");
                Assert.AreEqual(1.4f, plan.AttackSpeedMult, 1e-6);
                Assert.AreEqual(0.5, plan.HealthMult, 1e-9);
                Assert.AreEqual(17f, plan.HoldDistance, 1e-6, "ranged hold distance from the goal's ranged entry");
            }

            Assert.AreEqual(HoldWaveType.Finale, evt.BuildHoldWavePlan(objective, objective.TargetWaves, ElevenTusks())?.Type, "the finale is not mixed");
        }

        [TestMethod]
        public void MixedPlan_NoTargetStanding_TheBreachSquadSeeks_AndForcedTypesStillWork()
        {
            var (evt, objective) = MixedRun();
            var plan = evt.BuildHoldWavePlan(objective, 2, ElevenTusks());

            Assert.AreEqual(HoldWaveType.Mixed, plan.Type);
            Assert.IsFalse(plan.Roles.Contains(HoldRole.Breach), "nothing to breach: they seek instead");
            Assert.AreEqual(0u, plan.BreachTargetGuid);

            var (forcedEvt, forcedObjective) = MixedRun(HoldWaveType.Runners);
            forcedEvt.Spawner.AdoptProtectedForTest(BuildHoldCreature());

            Assert.AreEqual(HoldWaveType.Runners, forcedEvt.BuildHoldWavePlan(forcedObjective, 2, ElevenTusks())?.Type,
                "--wave-type still forces a one-type wave for testing");
        }

        [TestMethod]
        public void FinalePlan_TheBossIsTitled_AndSentAlone()
        {
            var (evt, objective) = MixedRun();
            evt.Spawner.AdoptProtectedForTest(BuildHoldCreature());

            var plan = evt.BuildHoldWavePlan(objective, objective.TargetWaves, ElevenTusks());

            Assert.AreEqual(HoldWaveType.Finale, plan.Type);
            Assert.AreNotEqual(0u, plan.ChampionWcid, "the finale always has a boss");
            Assert.IsTrue(plan.FinaleBossOnly, "the boss goes out alone first");
            StringAssert.StartsWith(plan.ChampionName, "Warlord ", plan.ChampionName);
            Assert.IsFalse(plan.ChampionName.Contains("Champion"), plan.ChampionName);
        }

        // ---- shipped content --------------------------------------------------------------------------------

        [TestMethod]
        public void ShippedGoals_UseTheMixAndTheLoudBoss_WhenPresent()
        {
            var goalsPath = FindContentFile(Path.Combine("ace-content", "events", "axes", "goals.json"));

            if (goalsPath == null)
                return;

            var store = WorldEventAxisStore.Parse(null, null, File.ReadAllText(goalsPath), null, null);

            foreach (var id in new[] { "defend_caravan", "defend_envoys" })
            {
                Assert.IsTrue(store.Goals.ContainsKey(id), string.Join(" | ", store.Diagnostics));
                var p = store.Goals[id].Protect;

                Assert.IsTrue(p.ShowHealthBar, id);
                Assert.IsNotNull(p.WaveMix, id);
                CollectionAssert.AreEqual(DefaultMix, p.WaveMix.Shares, id);
                Assert.IsTrue(p.WaveMix.Announce.Count >= 2, id);
                Assert.AreEqual(1.75, p.FinaleBossScale, 1e-9, id);
                Assert.AreEqual("Warlord", p.FinaleBossPrefix, id);

                var hold = p.SpecialWave(HoldWaveType.Ranged).HoldDistance;
                Assert.IsTrue(hold >= 15 && hold <= 20, $"{id}: ranged hold at {hold} m");

                foreach (var template in p.WaveMix.Announce)
                {
                    Assert.IsTrue(template.Contains("{side}"), $"{id}: the announcement names the breach side: {template}");

                    var line = WorldEventHoldRules.FillLine(WorldEventHoldRules.FillWaveTokens(template, "east", null, "Sir Donovan"),
                        "the Drudges", "envoy", "envoys", "Holtburg", 3, 3);
                    var broadcast = WorldEventAnnouncer.HoldPhaseLine("Wave 3 of 6", line);

                    AssertAscii(broadcast);
                    Assert.IsFalse(line.Contains('{') || line.Contains('}'), line);
                    Assert.IsTrue(SentenceCount(line) <= 3, line);
                    Assert.IsFalse(line.IndexOf("warp", StringComparison.OrdinalIgnoreCase) >= 0, line);
                }
            }

            Assert.IsTrue(store.Goals["defend_envoys"].Protect.WaveMix.Announce.All(l => l.Contains("{target}")),
                "the envoys' announcement names the knight the breach squad is hitting");
        }
    }
}
