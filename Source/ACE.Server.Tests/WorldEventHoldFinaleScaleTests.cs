using System;
using System.Collections.Generic;
using System.IO;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Defs;
using ACE.Server.WorldEvents.Hold;

namespace ACE.Server.Tests
{
    /// <summary>
    /// World Events Hold H5 review findings F1 and F6: the finale boss's size decision (WorldEventHoldFinaleBoss.DecideScale,
    /// pure) and the finale controller's tick when the boss cannot take a decision (WorldEventHoldFinaleController.TickCore).
    /// No dat, no PropertyManager, no landblock.
    /// </summary>
    [TestClass]
    public class WorldEventHoldFinaleScaleTests
    {
        private const float BossMultiplier = 1.6f;

        private static GoalDef ShippedEnvoys()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Content", "events", "axes", "goals.json")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, $"Could not find Content/events/axes/goals.json by walking up from {AppContext.BaseDirectory}");

            var store = WorldEventAxisStore.Load(Path.Combine(dir.FullName, "Content", "events", "axes"));
            Assert.IsTrue(store.Goals.TryGetValue("defend_envoys", out var goal), "defend_envoys did not load");

            return goal;
        }

        // ---- F1: the size decision ----------------------------------------------------------------------------------

        /// <summary>
        /// The shipped goal (finaleBossScale 0, no finale_scale set) with no readable height takes the World Events boss
        /// multiplier, never the old fixed 3.08.
        /// </summary>
        [TestMethod]
        public void ShippedGoal_WithNoReadableHeight_TakesTheBossMultiplier_NeverAFixed308()
        {
            var goal = ShippedEnvoys();
            var spawner = WorldEventHoldSpawnerTuning.From(WorldEventHoldSettingsResolver.Resolve(goal, _ => null), goal);

            Assert.AreEqual(0.0, spawner.FinaleScale, "control: the shipped goal asks for the height match");

            foreach (var (named, own) in new (double?, double?)[] { (null, null), (null, 2.0), (6.0, null) })
            {
                var scale = WorldEventHoldFinaleBoss.DecideScale(spawner.FinaleScale, named, own, 1.0, BossMultiplier,
                    spawner.FinaleHeightScaleMin, spawner.FinaleHeightScaleMax, out var how);

                Assert.AreEqual(BossMultiplier, scale, $"named={named} own={own}: {how}");
                Assert.AreNotEqual(3.08f, scale);
                Assert.AreNotEqual((float)spawner.FinaleFallbackScale, scale, "finale_fallback_scale is resolved but never applied");
            }

            // And with no boss multiplier either, the boss is its own size.
            Assert.AreEqual(1.0f, WorldEventHoldFinaleBoss.DecideScale(0, null, null, 1.0, 0f, 0.5, 12, out _));
        }

        [TestMethod]
        public void ShippedGoal_WithBothHeights_MatchesTheNamedBosses()
        {
            var goal = ShippedEnvoys();
            var spawner = WorldEventHoldSpawnerTuning.From(WorldEventHoldSettingsResolver.Resolve(goal, _ => null), goal);

            // Named bosses render 6 m tall; this model is 2 m at ObjScale 1.5 (3 m): x2 makes it 6 m.
            var scale = WorldEventHoldFinaleBoss.DecideScale(spawner.FinaleScale, 6.0, 2.0, 1.5, BossMultiplier,
                spawner.FinaleHeightScaleMin, spawner.FinaleHeightScaleMax, out var how);

            Assert.AreEqual(2.0f, scale, 1e-5, how);

            // Clamped to finale_height_scale_min / _max.
            Assert.AreEqual(12f, WorldEventHoldFinaleBoss.DecideScale(0, 600.0, 1.0, 1.0, BossMultiplier, 0.5, 12, out _));
            Assert.AreEqual(0.5f, WorldEventHoldFinaleBoss.DecideScale(0, 0.1, 10.0, 1.0, BossMultiplier, 0.5, 12, out _));
        }

        [TestMethod]
        public void AConfiguredScale_WinsOverTheHeights_WithinOneToTwoAndAHalf()
        {
            Assert.AreEqual(1.8f, WorldEventHoldFinaleBoss.DecideScale(1.8, 6.0, 2.0, 1.0, BossMultiplier, 0.5, 12, out var how), 1e-5);
            Assert.AreEqual("finale_scale", how);
            Assert.AreEqual(2.5f, WorldEventHoldFinaleBoss.DecideScale(9, null, null, 1.0, BossMultiplier, 0.5, 12, out _));
        }

        /// <summary>Review finding N3: a miss (no height measured) is never cached, so the next finale measures again.</summary>
        [TestMethod]
        public void OnlyARealMedian_IsCached()
        {
            Assert.IsFalse(WorldEventHoldFinaleBoss.CachesNamedHeight(null));
            Assert.IsFalse(WorldEventHoldFinaleBoss.CachesNamedHeight(WorldEventHoldFinaleBoss.MedianHeight(Array.Empty<double>())), "empty");
            Assert.IsFalse(WorldEventHoldFinaleBoss.CachesNamedHeight(0));
            Assert.IsFalse(WorldEventHoldFinaleBoss.CachesNamedHeight(double.NaN));
            Assert.IsTrue(WorldEventHoldFinaleBoss.CachesNamedHeight(4.2));
        }

        [TestMethod]
        public void NamedBossHeight_WritesTheCacheOnlyBehindTheGate()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Source", "ACE.Server", "WorldEvents", "Hold", "WorldEventHoldFinaleBoss.cs")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, $"Could not find WorldEventHoldFinaleBoss.cs by walking up from {AppContext.BaseDirectory}");

            var code = File.ReadAllText(Path.Combine(dir.FullName, "Source", "ACE.Server", "WorldEvents", "Hold", "WorldEventHoldFinaleBoss.cs")).Replace("\r\n", "\n");
            var gate = code.IndexOf("if (CachesNamedHeight(median))", StringComparison.Ordinal);
            var write = code.IndexOf("namedHeight = median;", StringComparison.Ordinal);

            Assert.IsTrue(gate >= 0, "the cache write is gated");
            Assert.IsTrue(write > gate, "the only cache write sits after the gate");
            Assert.AreEqual(write, code.LastIndexOf("namedHeight = median;", StringComparison.Ordinal), "one cache write");
        }

        [TestMethod]
        public void MedianHeight_AndRenderedHeight()
        {
            Assert.IsNull(WorldEventHoldFinaleBoss.MedianHeight(new double[] { 0, -1, double.NaN }));
            Assert.AreEqual(3.0, WorldEventHoldFinaleBoss.MedianHeight(new double[] { 5, 1, 3 }));
            Assert.AreEqual(2.5, WorldEventHoldFinaleBoss.MedianHeight(new double[] { 1, 2, 3, 4 }));
            Assert.AreEqual(2.0 * 1.5 * 1.6, WorldEventHoldFinaleBoss.RenderedHeight(2.0, 1.5, 1.6), 1e-9);
            Assert.AreEqual(2.0, WorldEventHoldFinaleBoss.RenderedHeight(2.0, 0, 0), 1e-9, "unusable scales count as 1");
        }

        // ---- F6: a decision the boss cannot take is not consumed --------------------------------------------------------

        private static readonly IReadOnlyList<(uint Guid, uint Current, uint Max)> NoKnights = Array.Empty<(uint, uint, uint)>();

        [TestMethod]
        public void OnPlayers_NotEnqueued_IsDecidedAgainNextTick()
        {
            var controller = new WorldEventHoldFinaleController(new Random(1));
            var tuning = WorldEventHoldTuning.Defaults;
            var enqueued = new List<HoldFinaleDecision>();

            // The boss has no landblock this tick: nothing is enqueued and nothing is consumed.
            var first = controller.TickCore(10, 0xA0000001, 1000, NoKnights, (0, 0), false, tuning, d => false);
            Assert.AreEqual(HoldFinaleDecisionKind.None, first.Kind);

            // Inside the once-a-second gate: still nothing.
            Assert.AreEqual(HoldFinaleDecisionKind.None, controller.TickCore(10.5, 0xA0000001, 1000, NoKnights, (0, 0), false, tuning, d => { enqueued.Add(d); return true; }).Kind);

            // A second later the landblock is back: the transition onto the players is decided again and delivered.
            var second = controller.TickCore(11, 0xA0000001, 1000, NoKnights, (0, 0), false, tuning, d => { enqueued.Add(d); return true; });
            Assert.AreEqual(HoldFinaleDecisionKind.OnPlayers, second.Kind);
            Assert.AreEqual(1, enqueued.Count);

            // Delivered once: the next tick has no transition left.
            Assert.AreEqual(HoldFinaleDecisionKind.None, controller.TickCore(12, 0xA0000001, 1000, NoKnights, (0, 0), false, tuning, d => { enqueued.Add(d); return true; }).Kind);
            Assert.AreEqual(1, enqueued.Count);
        }

        [TestMethod]
        public void Control_OnPlayers_Enqueued_IsConsumed()
        {
            var controller = new WorldEventHoldFinaleController(new Random(1));
            var tuning = WorldEventHoldTuning.Defaults;

            Assert.AreEqual(HoldFinaleDecisionKind.OnPlayers, controller.TickCore(10, 0xA0000001, 1000, NoKnights, (0, 0), false, tuning, d => true).Kind);
            Assert.AreEqual(HoldFinaleDecisionKind.None, controller.TickCore(11, 0xA0000001, 1000, NoKnights, (0, 0), false, tuning, d => true).Kind);
        }

        [TestMethod]
        public void ATarget_NotEnqueued_IsNotRemembered()
        {
            var controller = new WorldEventHoldFinaleController(new Random(1));
            var tuning = WorldEventHoldTuning.Defaults;
            var knights = new List<(uint, uint, uint)> { (0xB0000001, 500, 1000), (0xB0000002, 900, 1000) };

            Assert.AreEqual(HoldFinaleDecisionKind.None, controller.TickCore(10, 0xA0000001, 1000, knights, (0, 0), false, tuning, d => false).Kind);
            Assert.AreEqual(0u, controller.TargetGuid, "the pick was rolled back");

            var next = controller.TickCore(11, 0xA0000001, 1000, knights, (0, 0), false, tuning, d => true);
            Assert.AreEqual(HoldFinaleDecisionKind.Target, next.Kind);
            Assert.IsTrue(next.Changed, "still a change: the boss never heard about the first pick");
            Assert.AreEqual(next.TargetGuid, controller.TargetGuid);
        }
    }
}
