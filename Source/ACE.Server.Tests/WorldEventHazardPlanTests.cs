using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Server.HazardMechanics;
using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The pure planner of World Event hazards (WorldEventHazards.Plan / BuildStart / ResolveElement / RainHeightFor):
    /// which kind, which element, which ball patterns and which args a run gets. No PropertyManager read - the settings
    /// are a plain record.
    /// </summary>
    [TestClass]
    public class WorldEventHazardPlanTests
    {
        private static readonly WorldEventHazardSettings Settings = WorldEventHazardSettings.Defaults;

        private static Position Outdoor(float x, float y) => new Position(0xA9B40005, x, y, 50f, 0f, 0f, 0f, 1f, 0);

        private static WorldEventArena Arena(float x = 96f, float y = 96f, float radius = 60f) => new WorldEventArena(Outdoor(x, y), radius, radius / 2f);

        private static SourceThemeDef Source(HazardDef hazard = null, List<DecorDef> decor = null) => new SourceThemeDef
        {
            Id = "t",
            Hazard = hazard,
            Decor = decor ?? new List<DecorDef>(),
        };

        private static WorldEventHazardPlan PlanOf(SourceThemeDef source, string requested = null, WorldEventHazardSettings settings = null, WorldEventArena arena = null, int seed = 1)
            => WorldEventHazards.Plan(source, requested, settings ?? Settings, arena ?? Arena(), new Random(seed), out _);

        // ---- kind resolution ----------------------------------------------------------------------------------

        [TestMethod]
        public void SwitchOff_PlansNothing_AndSaysWhy()
        {
            var plan = WorldEventHazards.Plan(Source(new HazardDef { Kind = "wisps" }), null, Settings with { Enabled = false }, Arena(), new Random(1), out var note);

            Assert.IsNull(plan);
            Assert.IsTrue(note.Contains("switched off"), note);
        }

        [TestMethod]
        public void RequestNone_BeatsTheSourceDefault()
        {
            Assert.IsNull(PlanOf(Source(new HazardDef { Kind = "wisps" }), "none"));
        }

        [TestMethod]
        public void SourceKindNone_PlansNothing()
        {
            Assert.IsNull(PlanOf(Source(new HazardDef { Kind = "none" })));
        }

        [TestMethod]
        public void NoRequest_UsesTheSourceKind()
        {
            Assert.AreEqual("wisps", PlanOf(Source(new HazardDef { Kind = "wisps" })).Kind);
            Assert.AreEqual("rain", PlanOf(Source(new HazardDef { Kind = "rain" })).Kind);
        }

        [TestMethod]
        public void ExplicitRequest_BeatsTheSourceKind()
        {
            Assert.AreEqual("rain", PlanOf(Source(new HazardDef { Kind = "wisps" }), "rain").Kind);
            Assert.AreEqual("wisps", PlanOf(Source(new HazardDef { Kind = "rain" }), " WISPS ").Kind, "case and spaces do not matter");
        }

        [TestMethod]
        public void SourceWithoutAHazardBlock_IsRandom_AndEveryKindShowsUp()
        {
            var seen = new HashSet<string>();

            for (var seed = 1; seed <= 60; seed++)
                seen.Add(PlanOf(Source(), null, seed: seed).Kind);

            CollectionAssert.AreEquivalent(new[] { "wisps", "balls", "rain" }, seen.ToList());
        }

        [TestMethod]
        public void RequestRandom_PicksAnyKind_UnlikeAnAbsentRequest()
        {
            var seen = new HashSet<string>();

            for (var seed = 1; seed <= 60; seed++)
                seen.Add(PlanOf(Source(new HazardDef { Kind = "wisps" }), "random", seed: seed).Kind);

            Assert.IsTrue(seen.Count > 1, "random is not the source default");
        }

        [TestMethod]
        public void UnknownRequest_PlansNothing()
        {
            var plan = WorldEventHazards.Plan(Source(new HazardDef { Kind = "wisps" }), "meteors", Settings, Arena(), new Random(1), out var note);

            Assert.IsNull(plan);
            Assert.IsTrue(note.Contains("meteors"), note);
        }

        [TestMethod]
        public void IndoorArena_PlansNothing()
        {
            var indoor = new WorldEventArena(new Position(0x016C019E, 10f, 10f, 0f, 0f, 0f, 0f, 1f, 0), 60f, 30f);

            Assert.IsNull(PlanOf(Source(new HazardDef { Kind = "wisps" }), null, null, indoor));
        }

        [TestMethod]
        public void ArenaTooWideForOneSpan_PlansNothing()
        {
            // A 100 m radius is 200 m across: past the 192 m span a spanning region may have.
            Assert.IsNull(PlanOf(Source(new HazardDef { Kind = "wisps" }), null, null, Arena(radius: 100f)));
        }

        [TestMethod]
        public void SourceBallsDefault_AlternatesWithRain_ButAnExplicitBallsRequestPinsRolling()
        {
            var fromSource = PlanOf(Source(new HazardDef { Kind = "balls" }));
            var pinned = PlanOf(Source(new HazardDef { Kind = "wisps" }), "balls");

            Assert.AreEqual("balls", fromSource.Kind);
            Assert.IsTrue(fromSource.BallsAlternateWithRain);
            Assert.AreEqual("balls", pinned.Kind);
            Assert.IsFalse(pinned.BallsAlternateWithRain);
        }

        [TestMethod]
        public void NextRunKind_AlternatesOnlyForAnAlternatingPlan()
        {
            var alternating = PlanOf(Source(new HazardDef { Kind = "balls" }));
            var kinds = new HashSet<string>();
            var rng = new Random(5);

            for (var i = 0; i < 40; i++)
                kinds.Add(WorldEventHazards.NextRunKind(alternating, rng));

            CollectionAssert.AreEquivalent(new[] { "balls", "rain" }, kinds.ToList());

            var wisps = PlanOf(Source(new HazardDef { Kind = "wisps" }));

            for (var i = 0; i < 10; i++)
                Assert.AreEqual("wisps", WorldEventHazards.NextRunKind(wisps, rng));
        }

        // ---- the shipped defaults (2026-11-17 retune) ----------------------------------------------------------

        [TestMethod]
        public void ShippedDefaults_ArePinnedExactly()
        {
            Assert.AreEqual("burst=1.5 follow=1 follow_lag=2.4 follow_target=random hold=3 mark_dest=1 move=3.5 pct=0.23 pillar_radius=5 rest=2.5", WorldEventHazardSettings.DefaultWisps);
            Assert.AreEqual("speed=2.5 lead=4 wave_every=6 waves=50", WorldEventHazardSettings.DefaultBalls);
            Assert.AreEqual("burst_radius=3 duration=300 lead=0.2 speed=7 target_share=0.3 target_spread=8", WorldEventHazardSettings.DefaultRain);
        }

        [TestMethod]
        public void ShippedWispsLayer_CarriesItsOwnPct_WhichBeatsTheGlobalOne()
        {
            var plan = PlanOf(Source(new HazardDef { Kind = "wisps" }));
            var start = WorldEventHazards.BuildStart(plan, "wisps", WorldEventHazardElement.Fire, 50f, new Random(1), null);

            Assert.IsFalse(start.Args.Has("pct"));
            Assert.AreEqual(0.23, HazardArgs.Merge(start.Args, start.SettingsLayer).GetDouble("pct", -1, 0, 1), 1e-9);
        }

        // ---- kinds (a source that rolls several kinds) ---------------------------------------------------------

        private static HazardDef Portal(params string[] kinds) => new HazardDef { Kind = "wisps", Element = "fire", Kinds = kinds.ToList() };

        [TestMethod]
        public void Kinds_PlanWispsAndBallsRunsAcrossASeededSequence()
        {
            var plan = PlanOf(Source(Portal("wisps", "balls")));
            var rng = new Random(11);
            var kinds = Enumerable.Range(0, 200).Select(_ => WorldEventHazards.NextRunKind(plan, rng)).ToList();

            Assert.IsTrue(kinds.Contains("wisps"));
            Assert.IsTrue(kinds.Contains("balls"));
            Assert.IsTrue(kinds.Contains("rain"), "a picked balls keeps its balls/rain alternation");
            Assert.IsTrue(plan.BallPatterns.Count > 0, "balls need a pattern pool");

            foreach (var kind in kinds.Distinct())
            {
                var start = WorldEventHazards.BuildStart(plan, kind, WorldEventHazardElement.Fire, 50f, new Random(3), null);

                Assert.AreEqual(kind, start.Kind);
            }
        }

        [TestMethod]
        public void Kinds_WispsAndBallsAreChosenUniformly_BallsHalfRolling()
        {
            var plan = PlanOf(Source(Portal("wisps", "balls")));
            var rng = new Random(21);
            var counts = new Dictionary<string, int> { ["wisps"] = 0, ["balls"] = 0, ["rain"] = 0 };

            for (var i = 0; i < 4000; i++)
                counts[WorldEventHazards.NextRunKind(plan, rng)]++;

            Assert.AreEqual(2000, counts["wisps"], 160, "half the runs are wisps");
            Assert.AreEqual(1000, counts["balls"], 160, "a quarter are rolling balls");
            Assert.AreEqual(1000, counts["rain"], 160, "a quarter are rain");
        }

        [TestMethod]
        public void NoKinds_BehavesAsBefore_EveryRunIsTheSourceKind()
        {
            var plan = PlanOf(Source(new HazardDef { Kind = "wisps", Element = "fire" }));
            var rng = new Random(2);

            Assert.AreEqual(0, plan.KindChoices.Count);

            for (var i = 0; i < 30; i++)
                Assert.AreEqual("wisps", WorldEventHazards.NextRunKind(plan, rng));

            Assert.AreEqual(0, plan.BallPatterns.Count, "no balls pool for a wisps-only source");
        }

        [TestMethod]
        public void Kinds_AnExplicitRequestOverridesThem()
        {
            var plan = PlanOf(Source(Portal("wisps", "balls")), "rain");
            var rng = new Random(2);

            Assert.AreEqual(0, plan.KindChoices.Count);

            for (var i = 0; i < 30; i++)
                Assert.AreEqual("rain", WorldEventHazards.NextRunKind(plan, rng));
        }

        [TestMethod]
        public void Kinds_WithAnEmptyBallPool_TurnABallsPickIntoRain()
        {
            var settings = Settings with { Pct = 0.30, Balls = WorldEventHazardSettings.DefaultBalls + " pct=abc" };
            var plan = WorldEventHazards.Plan(Source(Portal("wisps", "balls")), null, settings, Arena(), new Random(1), out var note);
            var rng = new Random(4);
            var kinds = Enumerable.Range(0, 100).Select(_ => WorldEventHazards.NextRunKind(plan, rng)).Distinct().OrderBy(k => k).ToList();

            CollectionAssert.AreEqual(new[] { "rain", "wisps" }, kinds);
            StringAssert.Contains(note, "rain only");
        }

        [TestMethod]
        public void Kinds_ASingleBallsEntry_IsJustAnAlternatingBallsPlan()
        {
            var plan = PlanOf(Source(Portal("balls")));

            Assert.AreEqual("balls", plan.Kind);
            Assert.AreEqual(0, plan.KindChoices.Count);
            Assert.IsTrue(plan.BallsAlternateWithRain);
        }

        // ---- ball pattern pool --------------------------------------------------------------------------------

        [TestMethod]
        public void BallPool_OnlyHoldsPatternsWhoseWaveFitsTheHighCount()
        {
            var plan = PlanOf(Source(new HazardDef { Kind = "balls" }));
            var merged = HazardArgs.Merge(HazardArgs.Parse("pct=0.3 damage_type=fire"), plan.BallsLayer);

            Assert.IsTrue(plan.BallPatterns.Count > 0, "the shipped high count must leave some pattern to roll");

            foreach (var pattern in plan.BallPatterns)
            {
                Assert.IsNull(BallRules.Validate(plan.Region, merged, pattern), pattern);

                var p = BallRules.Read(merged, pattern);

                for (var seed = 1; seed <= 20; seed++)
                {
                    var size = BallPatterns.Plan(plan.Region, pattern, p, new Random(seed)).Routes.Count;

                    Assert.IsTrue(size <= plan.CountHigh, $"{pattern} seed {seed}: wave of {size} exceeds {plan.CountHigh}");
                }
            }
        }

        [TestMethod]
        public void BallPool_ExcludesTheWallPatterns_AtTheShippedHighCount()
        {
            var plan = PlanOf(Source(new HazardDef { Kind = "balls" }));

            CollectionAssert.DoesNotContain(plan.BallPatterns.ToList(), BallPatterns.Row);
            CollectionAssert.DoesNotContain(plan.BallPatterns.ToList(), BallPatterns.Cross);
        }

        [TestMethod]
        public void BallPool_KeepsThePatternsThatShrinkToTheCap_AndDropsTheOnesThatCannot()
        {
            // At a high count of 5 a ring (floor 3), a fan and random all shrink to fit; a row or cross wall is fixed by the geometry.
            var plan = PlanOf(Source(new HazardDef { Kind = "balls" }), null, Settings with { CountLow = 3, CountHigh = 5 });
            var pool = plan.BallPatterns.ToList();

            Assert.AreEqual("balls", plan.Kind);
            CollectionAssert.DoesNotContain(pool, BallPatterns.Row);
            CollectionAssert.DoesNotContain(pool, BallPatterns.Cross);
            CollectionAssert.Contains(pool, BallPatterns.RingOut);
        }

        [TestMethod]
        public void BallPool_GrowsWithTheHighCount()
        {
            var small = PlanOf(Source(new HazardDef { Kind = "balls" }), null, Settings with { CountHigh = 10 });
            var large = PlanOf(Source(new HazardDef { Kind = "balls" }), null, Settings with { CountHigh = 64 });

            Assert.IsTrue(large.BallPatterns.Count >= small.BallPatterns.Count);

            foreach (var p in small.BallPatterns)
                CollectionAssert.Contains(large.BallPatterns.ToList(), p);
        }

        [TestMethod]
        public void BallPool_AtAHighCountOfOne_DropsTheRingsBelowTheirFloorOfThree()
        {
            var plan = WorldEventHazards.Plan(Source(new HazardDef { Kind = "balls" }), null, Settings with { CountLow = 1, CountHigh = 1 }, Arena(), new Random(1), out _);
            var pool = plan.BallPatterns.ToList();

            Assert.AreEqual("balls", plan.Kind, "fan and random still shrink to one ball");
            CollectionAssert.DoesNotContain(pool, BallPatterns.RingOut);
            CollectionAssert.DoesNotContain(pool, BallPatterns.Row);
            CollectionAssert.DoesNotContain(pool, BallPatterns.Cross);
        }

        // ---- element ------------------------------------------------------------------------------------------

        [TestMethod]
        public void Element_FixedWinsOverStyleAndRandom()
        {
            var plan = PlanOf(Source(new HazardDef { Kind = "wisps", Element = "acid", StyleElements = new List<string> { "fire" } }));

            Assert.AreEqual(WorldEventHazardElement.Acid, WorldEventHazards.ResolveElement(plan, 0, new Random(1)));
        }

        [DataTestMethod]
        [DataRow(0, WorldEventHazardElement.Frost)]
        [DataRow(1, WorldEventHazardElement.Acid)]
        [DataRow(2, WorldEventHazardElement.Fire)]
        [DataRow(3, WorldEventHazardElement.Lightning)]
        public void Element_FollowsTheDecorStyleIndex(int index, WorldEventHazardElement expected)
        {
            var plan = PlanOf(Source(new HazardDef { Kind = "balls", StyleElements = new List<string> { "frost", "acid", "fire", "lightning" } }));

            Assert.AreEqual(expected, WorldEventHazards.ResolveElement(plan, index, new Random(1)));
        }

        [TestMethod]
        public void Element_NoStylePicked_OrOutOfRange_IsRandom_AndAllFiveShowUp()
        {
            var plan = PlanOf(Source(new HazardDef { Kind = "balls", StyleElements = new List<string> { "frost" } }));
            var seen = new HashSet<WorldEventHazardElement>();

            for (var seed = 1; seed <= 200; seed++)
            {
                seen.Add(WorldEventHazards.ResolveElement(plan, -1, new Random(seed)));
                seen.Add(WorldEventHazards.ResolveElement(plan, 7, new Random(seed)));
            }

            Assert.AreEqual(5, seen.Count);
        }

        [TestMethod]
        public void Element_SourceWithNoElementAtAll_IsRandom()
        {
            var plan = PlanOf(Source());
            var seen = new HashSet<WorldEventHazardElement>();

            for (var seed = 1; seed <= 200; seed++)
                seen.Add(WorldEventHazards.ResolveElement(plan, -1, new Random(seed)));

            Assert.AreEqual(5, seen.Count);
        }

        // ---- rain height --------------------------------------------------------------------------------------

        [TestMethod]
        public void RainHeight_IsTheLowestPositiveSkyDecorDz()
        {
            var decor = new List<DecorDef>
            {
                new DecorDef { Wcid = 1, Dz = 67.7f },
                new DecorDef { Wcid = 2, Dz = 32.4f },
                new DecorDef { Wcid = 3, Dz = 0f },
                new DecorDef { Wcid = 4, Dz = 2f, Snap = true },
                new DecorDef { Wcid = 5, Dz = -5f },
            };

            Assert.AreEqual(32.4f, WorldEventHazards.RainHeightFor(Source(new HazardDef { Kind = "rain", RainHeight = 12f }, decor), Settings), 1e-4);
        }

        [TestMethod]
        public void RainHeight_NoSkyDecor_UsesTheSourceRainHeight_ThenTheSetting()
        {
            Assert.AreEqual(12f, WorldEventHazards.RainHeightFor(Source(new HazardDef { Kind = "rain", RainHeight = 12f }), Settings), 1e-4);
            Assert.AreEqual(30f, WorldEventHazards.RainHeightFor(Source(new HazardDef { Kind = "rain" }), Settings), 1e-4);
            Assert.AreEqual(44f, WorldEventHazards.RainHeightFor(Source(), Settings with { RainHeight = 44.0 }), 1e-4);
        }

        [TestMethod]
        public void RainHeight_GroundDecorOnly_DoesNotCountAsSkyDecor()
        {
            var decor = new List<DecorDef> { new DecorDef { Wcid = 1, Dz = 5f, Snap = true } };

            Assert.AreEqual(30f, WorldEventHazards.RainHeightFor(Source(null, decor), Settings), 1e-4);
        }

        // ---- the start ----------------------------------------------------------------------------------------

        private static string Keys(HazardArgs a) => string.Join(",", a.Keys.OrderBy(k => k, StringComparer.Ordinal));

        [TestMethod]
        public void BuildStart_Wisps_EmitsItsKeys_AndPassesTheMechanicsValidate_ForEveryElement()
        {
            var plan = PlanOf(Source(new HazardDef { Kind = "wisps" }));

            foreach (WorldEventHazardElement element in Enum.GetValues(typeof(WorldEventHazardElement)))
            {
                var start = WorldEventHazards.BuildStart(plan, "wisps", element, 50f, new Random(1), null);
                var visuals = WorldEventHazardElements.For(element);

                // no pct: the shipped wisps layer carries its own (pct=0.23), so BuildStart omits it and the layer wins
                Assert.AreEqual("cycles,damage_type,hit_verb,mark_script,script,wisp_wcid", Keys(start.Args));
                Assert.AreEqual(visuals.MarkerScript.ToString(), start.Args.GetString("mark_script"));
                Assert.AreEqual("wisps", start.Kind);
                Assert.AreEqual("mixed", start.Pattern);
                Assert.AreEqual(visuals.WispWcid.ToString(), start.Args.GetString("wisp_wcid"));
                Assert.AreEqual(visuals.HitVerb, start.Args.GetString("hit_verb"));
                Assert.AreEqual(WorldEventHazards.DamageWord(visuals.DamageType), start.Args.GetString("damage_type"));
                Assert.AreEqual(0.23, plan.WispsLayer.GetDouble("pct", -1, 0, 1), 1e-9);
                Assert.AreSame(plan.WispsLayer, start.SettingsLayer);

                var merged = HazardArgs.Merge(start.Args, start.SettingsLayer);

                Assert.IsNull(WispRules.Validate(start.Region, merged, start.Pattern), element.ToString());
            }
        }

        [TestMethod]
        public void BuildStart_Wisps_CyclesFillTheRunCap()
        {
            var plan = PlanOf(Source(new HazardDef { Kind = "wisps" }));
            var start = WorldEventHazards.BuildStart(plan, "wisps", WorldEventHazardElement.Fire, 50f, new Random(1), null);
            var merged = HazardArgs.Merge(start.Args, start.SettingsLayer);

            var fit = WispRules.MaxCyclesWithin(plan.Region, merged, "mixed");

            Assert.AreEqual(Math.Clamp(fit, 1, 500), start.Args.GetInt("cycles", -1, 0, 1000));
        }

        [TestMethod]
        public void BuildStart_Balls_EmitsItsKeys_AndEveryPoolPatternPassesValidate()
        {
            var plan = PlanOf(Source(new HazardDef { Kind = "balls" }));

            foreach (WorldEventHazardElement element in Enum.GetValues(typeof(WorldEventHazardElement)))
            {
                for (var seed = 1; seed <= 12; seed++)
                {
                    var start = WorldEventHazards.BuildStart(plan, "balls", element, 50f, new Random(seed), null);

                    Assert.AreEqual("ball_wcid,damage_type,hit_verb,marker_script,pct", Keys(start.Args));
                    Assert.AreEqual(WorldEventHazardElements.For(element).MarkerScript.ToString(), start.Args.GetString("marker_script"));
                    CollectionAssert.Contains(plan.BallPatterns.ToList(), start.Pattern);
                    Assert.AreEqual(WorldEventHazardElements.For(element).BallWcid.ToString(), start.Args.GetString("ball_wcid"));
                    Assert.IsNull(BallRules.Validate(start.Region, HazardArgs.Merge(start.Args, start.SettingsLayer), start.Pattern), $"{element} {start.Pattern}");
                }
            }
        }

        [TestMethod]
        public void BuildStart_Rain_EmitsItsKeys_AndDropZ()
        {
            var plan = PlanOf(Source(new HazardDef { Kind = "rain" }));
            var start = WorldEventHazards.BuildStart(plan, "rain", WorldEventHazardElement.Earth, 50.5f, new Random(1), null);
            var visuals = WorldEventHazardElements.For(WorldEventHazardElement.Earth);

            Assert.AreEqual("ball_wcid,burst_script,damage_type,drop_z,hit_verb,marker_script,pct", Keys(start.Args));
            Assert.IsNull(start.Pattern);
            Assert.AreEqual("80.5", start.Args.GetString("drop_z"), "anchor z 50.5 + the 30 m setting");
            Assert.AreEqual("bludgeon", start.Args.GetString("damage_type"));
            Assert.AreEqual("crushes", start.Args.GetString("hit_verb"));
            Assert.AreEqual(visuals.MarkerScript.ToString(), start.Args.GetString("marker_script"));
            Assert.AreEqual(visuals.BurstScript.ToString(), start.Args.GetString("burst_script"));
            Assert.AreSame(plan.RainLayer, start.SettingsLayer);
        }

        [TestMethod]
        public void BuildStart_UsesThePctSetting()
        {
            var plan = PlanOf(Source(new HazardDef { Kind = "wisps" }), null, Settings with { Pct = 0.45, Wisps = WispsNoPct });

            Assert.AreEqual("0.45", WorldEventHazards.BuildStart(plan, "wisps", WorldEventHazardElement.Fire, 0f, new Random(1), null).Args.GetString("pct"));
        }

        // ---- per-kind pct ---------------------------------------------------------------------------------------

        private static readonly string[] Kinds = { "wisps", "balls", "rain" };

        /// <summary>The shipped wisps string carries its own pct (0.23); these tests need a layer with none.</summary>
        private static readonly string WispsNoPct = WorldEventHazardSettings.DefaultWisps.Replace(" pct=0.23", "");

        private static WorldEventHazardSettings WithLayers(string wisps, string balls, string rain)
            => Settings with { Pct = 0.30, Wisps = wisps, Balls = balls, Rain = rain };

        [TestMethod]
        public void BuildStart_LayerPctWinsForThatKind_AndOmitsThePctArg()
        {
            var plan = PlanOf(Source(new HazardDef { Kind = "wisps" }), null,
                WithLayers(WispsNoPct + " pct=0.05", WorldEventHazardSettings.DefaultBalls + " pct=0.2", WorldEventHazardSettings.DefaultRain + " pct=0.1"));
            var expected = new Dictionary<string, double> { ["wisps"] = 0.05, ["balls"] = 0.2, ["rain"] = 0.1 };

            foreach (var kind in Kinds)
            {
                var start = WorldEventHazards.BuildStart(plan, kind, WorldEventHazardElement.Fire, 50f, new Random(1), null);

                Assert.IsFalse(start.Args.Has("pct"), kind + ": the event must not pass a pct that would beat the layer's");

                // HazardManager.Start's merge: the event's args over the kind's settings layer
                var merged = HazardArgs.Merge(start.Args, start.SettingsLayer);

                Assert.AreEqual(expected[kind], merged.GetDouble("pct", -1, 0, 1), 1e-9, kind);
            }
        }

        [TestMethod]
        public void BuildStart_OnlyTheKindWhoseLayerHasPct_Overrides()
        {
            var plan = PlanOf(Source(new HazardDef { Kind = "wisps" }), null,
                WithLayers(WispsNoPct, WorldEventHazardSettings.DefaultBalls, WorldEventHazardSettings.DefaultRain + " pct=0.1"));

            Assert.AreEqual("0.3", WorldEventHazards.BuildStart(plan, "wisps", WorldEventHazardElement.Fire, 50f, new Random(1), null).Args.GetString("pct"));
            Assert.AreEqual("0.3", WorldEventHazards.BuildStart(plan, "balls", WorldEventHazardElement.Fire, 50f, new Random(1), null).Args.GetString("pct"));
            Assert.IsFalse(WorldEventHazards.BuildStart(plan, "rain", WorldEventHazardElement.Fire, 50f, new Random(1), null).Args.Has("pct"));
        }

        [TestMethod]
        public void BuildStart_GlobalPctWhenTheLayerHasNone_ForEveryKind()
        {
            var plan = PlanOf(Source(new HazardDef { Kind = "wisps" }), null, Settings with { Pct = 0.45, Wisps = WispsNoPct });

            foreach (var kind in Kinds)
            {
                var start = WorldEventHazards.BuildStart(plan, kind, WorldEventHazardElement.Fire, 50f, new Random(1), null);

                Assert.AreEqual("0.45", start.Args.GetString("pct"), kind);
                Assert.AreEqual(0.45, HazardArgs.Merge(start.Args, start.SettingsLayer).GetDouble("pct", -1, 0, 1), 1e-9, kind);
            }
        }

        [TestMethod]
        public void Plan_PoolIsBuiltFromTheArgsTheRunGets_SoAnInvalidLayerPctPlansRainOnly()
        {
            var settings = Settings with { Pct = 0.30, Balls = WorldEventHazardSettings.DefaultBalls + " pct=abc" };
            var plan = WorldEventHazards.Plan(Source(new HazardDef { Kind = "balls" }), null, settings, Arena(), new Random(1), out var note);

            Assert.AreEqual(0, plan.BallPatterns.Count, "every start would be refused with that layer, so no pattern may be pooled");
            Assert.AreEqual("rain", plan.Kind);
            StringAssert.Contains(note, "rain only");

            var control = WorldEventHazards.Plan(Source(new HazardDef { Kind = "balls" }), null, Settings with { Balls = WorldEventHazardSettings.DefaultBalls + " pct=0.2" }, Arena(), new Random(1), out _);

            Assert.IsTrue(control.BallPatterns.Count > 0, "control: a valid layer pct keeps the pool");
        }

        [TestMethod]
        public void BuildStart_UnknownKind_Throws()
        {
            var plan = PlanOf(Source(new HazardDef { Kind = "wisps" }));

            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => WorldEventHazards.BuildStart(plan, "beams", WorldEventHazardElement.Fire, 0f, new Random(1), null));
        }

        [TestMethod]
        public void Region_IsTheArenaCircle_WithTheAbsoluteBand()
        {
            var plan = PlanOf(Source(new HazardDef { Kind = "wisps" }));
            var r = plan.Region;

            Assert.IsTrue(r.SpansLandblocks);
            Assert.IsTrue(r.HitsRequireInside);
            Assert.AreEqual(0xA9B4, r.Landblock);
            Assert.AreEqual(96f, r.CenterX, 1e-4);
            Assert.AreEqual(60f, r.Radius, 1e-4);
            Assert.AreEqual(20f, r.AbsoluteBandMinZ.Value, 1e-4, "z 50 - half height 30");
            Assert.AreEqual(80f, r.AbsoluteBandMaxZ.Value, 1e-4);
        }

        [TestMethod]
        public void Damage_WordsMatchTheMechanicsAcceptedWords()
        {
            foreach (WorldEventHazardElement element in Enum.GetValues(typeof(WorldEventHazardElement)))
            {
                var word = WorldEventHazards.DamageWord(WorldEventHazardElements.For(element).DamageType);

                Assert.IsTrue(new[] { "fire", "acid", "cold", "electric", "bludgeon" }.Contains(word), word);
            }
        }
    }
}
