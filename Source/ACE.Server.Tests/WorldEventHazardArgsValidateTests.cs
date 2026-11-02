using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity;
using ACE.Server.HazardMechanics;
using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// HAZARDS-PLAN R: the exact args and settings layer that B1's real builder (WorldEventHazards.Plan + BuildStart) hands
    /// HazardManager.Start must pass the mechanic's own Validate on the world event arena (HazardRegion.ArenaCircle, r=60),
    /// for every kind x element at the low and the high count, with every ball pattern of the pool, and for an arena that
    /// crosses a landblock corner. Also: the wcid the mechanic will require is the element's wcid (a key-name mismatch between
    /// what B1 emits and what a mechanic reads would show here as a default wcid). No PropertyManager read, so this class
    /// passes alone.
    /// </summary>
    [TestClass]
    public class WorldEventHazardArgsValidateTests
    {
        private const float AnchorZ = 50f;

        private static readonly WorldEventHazardElement[] Elements = (WorldEventHazardElement[])Enum.GetValues(typeof(WorldEventHazardElement));

        // The shipped low (4) and high (10) counts, plus the setting's own ceiling (64) so the 25-ball row and cross walls are in a pool.
        private static readonly int[] Counts = { WorldEventHazardSettings.DefaultCountLow, WorldEventHazardSettings.DefaultCountHigh, 64 };

        private static Position Outdoor(float x, float y) => new Position(0xA9B40005, x, y, AnchorZ, 0f, 0f, 0f, 1f, 0);

        private static WorldEventArena Arena(float x, float y) => new WorldEventArena(Outdoor(x, y), 60f, 30f);

        /// <summary>The centre, and one that puts the circle across the north-east corner of the anchor landblock.</summary>
        private static IEnumerable<(string Name, WorldEventArena Arena)> Arenas()
        {
            yield return ("centre", Arena(96f, 96f));
            yield return ("corner", Arena(180f, 180f));
        }

        private static SourceThemeDef Source(string kind, WorldEventHazardElement element) => new SourceThemeDef
        {
            Id = "t",
            Hazard = new HazardDef { Kind = kind, Element = element.ToString().ToLowerInvariant() },
            Decor = new List<DecorDef>(),
        };

        private static WorldEventHazardPlan PlanOf(string kind, WorldEventHazardElement element, int count, WorldEventArena arena)
        {
            var settings = WorldEventHazardSettings.Defaults with { CountLow = count, CountHigh = count };
            var plan = WorldEventHazards.Plan(Source(kind, element), kind, settings, arena, new Random(1), out var note);

            Assert.IsNotNull(plan, $"{kind}/{element}/{count}: {note}");
            Assert.IsNull(plan.Region.Validate(), "fixture: the arena region itself validates");
            Assert.IsTrue(plan.Region.SpansLandblocks);

            return plan;
        }

        private static uint ExpectedWcid(WorldEventHazardVisuals v, string kind) => kind == WorldEventHazards.KindWisps ? v.WispWcid : v.BallWcid;

        private static void AssertValidates(WorldEventHazardStart start, WorldEventHazardElement element, string what)
        {
            Assert.IsTrue(HazardMechanicRegistry.TryCreate(start.Kind, out var mechanic), what + ": the kind is registered");

            // exactly the merge HazardManager.Start does: the caller's args over the settings layer
            var merged = HazardArgs.Merge(start.Args, start.SettingsLayer);
            var patterns = mechanic.Patterns ?? Array.Empty<string>();

            if (patterns.Count == 0)
                Assert.IsNull(start.Pattern, what + ": the mechanic has no patterns, so none is asked for");
            else
                Assert.IsTrue(patterns.Contains(start.Pattern), $"{what}: pattern '{start.Pattern}' is one of the mechanic's ({string.Join(",", patterns)})");

            var error = mechanic.Validate(start.Region, merged, start.Pattern);

            Assert.IsNull(error, $"{what}: {error} [args {merged}]");

            var visuals = WorldEventHazardElements.For(element);
            var wanted = ExpectedWcid(visuals, start.Kind);

            CollectionAssert.Contains(mechanic.RequiredWcids(merged).ToList(), wanted, $"{what}: the mechanic requires the element's own wcid {wanted} (a name mismatch would fall back to the default weenie)");

            // The keys the mechanic reads for scripts and heights must be the ones B1 emits: a misspelt key is silently the default.
            if (start.Kind == WorldEventHazards.KindWisps)
            {
                var w = WispSettings.From(merged);

                Assert.AreEqual(visuals.PillarScript, w.Script, what + ": the wisp reads the element's pillar script");
                Assert.AreEqual(visuals.MarkerScript, w.MarkScript, what + ": the wisp reads the element's mark_script");
                Assert.AreEqual(int.Parse(start.Args.GetString("cycles")), w.Cycles, what + ": the wisp reads the emitted cycles");
            }
            else if (start.Kind == WorldEventHazards.KindBalls)
            {
                Assert.AreEqual(visuals.MarkerScript, BallRules.Read(merged, start.Pattern).MarkerScript, what + ": the balls read the element's marker_script");
            }
            else if (start.Kind == WorldEventHazards.KindRain)
            {
                var r = RainSettings.From(merged);

                Assert.AreEqual(visuals.MarkerScript, r.MarkerScript, what + ": the rain reads the element's marker script");
                Assert.AreEqual(visuals.BurstScript, r.BurstScript, what + ": the rain reads the element's burst script");
                Assert.IsTrue(r.DropZ.HasValue, what + ": the rain reads the emitted drop_z");
                Assert.AreEqual(double.Parse(start.Args.GetString("drop_z"), System.Globalization.CultureInfo.InvariantCulture), r.DropZ.Value, 1e-6, what + ": the rain reads drop_z");
            }

            Assert.AreEqual(visuals.DamageType, HazardContext.TryParseDamageType(merged.GetString(HazardDefaults.DamageTypeKey)), what + ": the damage type word round-trips to the element's type");
        }

        [TestMethod]
        public void EveryKindAndElement_AtTheLowAndHighCount_PassesTheMechanicsValidate()
        {
            var checkedStarts = 0;

            foreach (var (arenaName, arena) in Arenas())
            {
                foreach (var element in Elements)
                {
                    foreach (var count in Counts)
                    {
                        foreach (var kind in new[] { WorldEventHazards.KindWisps, WorldEventHazards.KindRain })
                        {
                            var plan = PlanOf(kind, element, count, arena);
                            var start = WorldEventHazards.BuildStart(plan, kind, element, AnchorZ, new Random(1), null);

                            AssertValidates(start, element, $"{arenaName}/{kind}/{element}/count {count}");
                            checkedStarts++;
                        }
                    }
                }
            }

            Assert.AreEqual(2 * 5 * 3 * 2, checkedStarts);
        }

        [TestMethod]
        public void BallsWithEveryPoolPattern_PassTheBallsValidate()
        {
            var patternsSeen = new HashSet<string>();
            var checkedStarts = 0;

            foreach (var (arenaName, arena) in Arenas())
            {
                foreach (var element in Elements)
                {
                    foreach (var count in Counts)
                    {
                        var plan = PlanOf(WorldEventHazards.KindBalls, element, count, arena);

                        // a count too small for any wave plans rain instead (pinned by the planner's own tests); rain is covered above
                        if (plan.Kind != WorldEventHazards.KindBalls)
                        {
                            Assert.AreEqual(0, plan.BallPatterns.Count, $"{arenaName}/{element}/count {count}: only an empty pool falls back to rain");
                            continue;
                        }

                        var pool = plan.BallPatterns.ToList();
                        var seen = new HashSet<string>();

                        // BuildStart draws the pattern from the pool at random: step the seed until every member has been built
                        for (var seed = 1; seed <= 400 && seen.Count < pool.Count; seed++)
                        {
                            var start = WorldEventHazards.BuildStart(plan, WorldEventHazards.KindBalls, element, AnchorZ, new Random(seed), null);

                            AssertValidates(start, element, $"{arenaName}/balls/{element}/count {count}/{start.Pattern}");
                            seen.Add(start.Pattern);
                            patternsSeen.Add(start.Pattern);
                            checkedStarts++;
                        }

                        CollectionAssert.AreEquivalent(pool, seen.ToList(), $"{arenaName}/{element}/count {count}: every pool pattern was built");
                    }
                }
            }

            Assert.IsTrue(checkedStarts > 0, "at least one balls start was built");
            Assert.IsTrue(patternsSeen.Count > 1, "more than one ball pattern is in play across the counts: " + string.Join(",", patternsSeen));
        }

        [TestMethod]
        public void TheShippedHighCount_HasANonEmptyBallPool_ForEveryElement()
        {
            foreach (var element in Elements)
            {
                var plan = PlanOf(WorldEventHazards.KindBalls, element, WorldEventHazardSettings.DefaultCountHigh, Arena(96f, 96f));

                Assert.AreEqual(WorldEventHazards.KindBalls, plan.Kind, $"{element}: balls stay balls at the shipped high count");
                Assert.IsTrue(plan.BallPatterns.Count > 0);
            }
        }

        [TestMethod]
        public void TheDamageTypeTheBuilderEmits_IsOneTheMechanicsAccept()
        {
            // BadDamageType is the shared refusal; a word B1 emitted that it refused would fail every Validate above, but say it directly too
            foreach (var element in Elements)
            {
                var plan = PlanOf(WorldEventHazards.KindRain, element, 10, Arena(96f, 96f));
                var start = WorldEventHazards.BuildStart(plan, WorldEventHazards.KindRain, element, AnchorZ, new Random(1), null);

                Assert.IsNull(HazardContext.BadDamageType(start.Args), element.ToString());
            }
        }
    }
}
