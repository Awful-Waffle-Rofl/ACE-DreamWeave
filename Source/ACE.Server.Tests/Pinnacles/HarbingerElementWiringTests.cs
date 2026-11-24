using System;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Server.HazardMechanics;
using ACE.Server.Pinnacles;
using ACE.Server.Pinnacles.Harbinger;
using ACE.Server.Pinnacles.Matron;
using ACE.Server.WorldEvents;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.Pinnacles
{
    /// <summary>
    /// Each element's hazard run carries its own identity (final review C1, I1): the args that actually reach StartHazard
    /// in a phase run and in the final name the element's damage type, weenies and effect scripts, and the acid runs on the
    /// Matron's damage curve and submerged tier. Expected values are literals, so a change to the shared tables shows here.
    /// Reads no PropertyManager value (the script reads the clamp from its settings record).
    /// </summary>
    [TestClass]
    public class HarbingerElementWiringTests
    {
        private static readonly HarbingerSettings S = HarbingerSettings.Defaults;

        /// <summary>The args StartHazard received for <paramref name="element"/>'s run, in its own phase or in the final.</summary>
        private static FakePinnacleFightPort.HazardCall RunOf(HarbingerElement element, bool final)
        {
            var p = new FakePinnacleFightPort(1);
            var s = new HarbingerScript(S);
            s.OnStart(p);
            s.EnterPhase(final ? 5 : (int)element + 1, p);

            var def = HarbingerData.Element(element);
            return p.Hazards.Single(h => h.Tag == def.Tag);
        }

        private static readonly (HarbingerElement Element, string Word, string HitVerb)[] Identity =
        {
            (HarbingerElement.Lightning, "electric", "jolts"),
            (HarbingerElement.Frost, "cold", "freezes"),
            (HarbingerElement.Fire, "fire", "sears"),
        };

        [TestMethod]
        public void EveryNonAcidRun_CarriesItsDamageTypeAndHitVerb_InPhaseAndFinal()
        {
            foreach (var (element, word, verb) in Identity)
                foreach (var final in new[] { false, true })
                {
                    var args = RunOf(element, final).Args;
                    var what = $"{element} final={final}";

                    Assert.AreEqual(word, args.GetString(HazardDefaults.DamageTypeKey), what);
                    Assert.AreEqual(verb, args.GetString(HazardDefaults.HitVerbKey), what);
                    Assert.AreEqual(WorldEventHazards.DamageWord(HarbingerData.Element(element).DamageType), word, what + ": the def is the source");
                }
        }

        [TestMethod]
        public void Lightning_IsTheStormWisp_WithTheLightningPillarAndMarker()
        {
            foreach (var final in new[] { false, true })
            {
                var run = RunOf(HarbingerElement.Lightning, final);

                Assert.AreEqual(HazardMechanicRegistry.Wisps, run.Kind);
                Assert.AreEqual(1006963, run.Args.GetInt("wisp_wcid", 0, 0, int.MaxValue), $"final={final}");
                Assert.AreEqual(HarbingerData.StormWispWcid, (uint)run.Args.GetInt("wisp_wcid", 0, 0, int.MaxValue));
                Assert.AreEqual(0x3300116Cu, HazardToolRoutes.ParseScript(run.Args.GetString("script"), 0), $"final={final}: Pillar of Lightning");
                Assert.AreEqual(0x33000797u, HazardToolRoutes.ParseScript(run.Args.GetString("mark_script"), 0), $"final={final}: purple beam");
            }
        }

        [TestMethod]
        public void Frost_IsTheHailstone_WithTheFrostMarkerAndBurst()
        {
            foreach (var final in new[] { false, true })
            {
                var run = RunOf(HarbingerElement.Frost, final);

                Assert.AreEqual(HazardMechanicRegistry.Rain, run.Kind);
                Assert.AreEqual(1008263, run.Args.GetInt("ball_wcid", 0, 0, int.MaxValue), $"final={final}: the small hailstone, not the def's frost ball");
                Assert.AreEqual(0x3300079Au, HazardToolRoutes.ParseScript(run.Args.GetString("marker_script"), 0), $"final={final}: blue beam");
                Assert.AreEqual(0x3300116Bu, HazardToolRoutes.ParseScript(run.Args.GetString("burst_script"), 0), $"final={final}: Pillar of Frost");
            }
        }

        [TestMethod]
        public void Fire_IsTheFireBall_WithTheFireMarker()
        {
            foreach (var final in new[] { false, true })
            {
                var run = RunOf(HarbingerElement.Fire, final);

                Assert.AreEqual(HazardMechanicRegistry.Balls, run.Kind);
                Assert.AreEqual(1006966, run.Args.GetInt("ball_wcid", 0, 0, int.MaxValue), $"final={final}");
                Assert.AreEqual(HarbingerData.FireBallWcid, (uint)run.Args.GetInt("ball_wcid", 0, 0, int.MaxValue));
                Assert.AreEqual(HazardDefaults.WispDestMarkScript, HazardToolRoutes.ParseScript(run.Args.GetString("marker_script"), 0), $"final={final}");
            }
        }

        private static HazardRun Run(string kind, string pattern, HazardArgs args)
        {
            Assert.IsTrue(HazardMechanicRegistry.TryCreate(kind, out var mechanic));
            Assert.IsTrue(HazardRegionPresets.TryGet("harbinger", 0, out var region));
            return new HazardRun(1, kind, pattern, region, args, 1, null, mechanic, null, DateTime.UtcNow);
        }

        [TestMethod]
        public void TheHazardContext_ResolvesElectricForLightning_AndColdForFrost()
        {
            Assert.AreEqual(DamageType.Electric, Run(HazardMechanicRegistry.Wisps, "mixed", S.LightningArgs(S.WispPct, false)).Context.DamageType);
            Assert.AreEqual(DamageType.Electric, Run(HazardMechanicRegistry.Wisps, "mixed", S.LightningArgs(S.WispPct, true)).Context.DamageType);
            Assert.AreEqual(DamageType.Cold, Run(HazardMechanicRegistry.Rain, null, S.HailArgs(S.HailPct, false)).Context.DamageType);
            Assert.AreEqual(DamageType.Cold, Run(HazardMechanicRegistry.Rain, null, S.HailArgs(S.HailPct, true)).Context.DamageType);
            Assert.AreEqual(DamageType.Fire, Run(HazardMechanicRegistry.Balls, BallPatterns.RingAlt, S.FireArgs(S.FirePct, false)).Context.DamageType);
        }

        // ---- the acid: the Matron's curve and submerged tier ----

        [TestMethod]
        public void Acid_CarriesTheMatronDamageKeys_InPhaseAndFinal()
        {
            foreach (var final in new[] { false, true })
            {
                var args = RunOf(HarbingerElement.Acid, final).Args;
                var what = $"final={final}";

                Assert.AreEqual(10.0, args.GetDouble("damage", -1, -1, 1e6), what);
                Assert.AreEqual(10.0, args.GetDouble("damage_per_m", -1, -1, 1e6), what);
                Assert.AreEqual(250.0, args.GetDouble("damage_max", -1, -1, 1e6), what);
                Assert.AreEqual(2.0, args.GetDouble("head_height", -1, -1, 1e6), what);
                Assert.AreEqual(139.0, args.GetDouble("submerged_damage", -1, -1, 1e6), what);
                Assert.AreEqual(25.0, args.GetDouble("submerged_per_m", -1, -1, 1e6), what);
                Assert.AreEqual(500.0, args.GetDouble("submerged_max", -1, -1, 1e6), what);
            }
        }

        [TestMethod]
        public void Acid_FlatDamage_EqualsTheMatronsAtOneAndFiveMetres()
        {
            Assert.IsTrue(HazardRegionPresets.TryGet("harbinger", 0, out var region));
            var harbinger = FillSettings.From(S.AcidArgs(HarbingerSettings.AcidFloorZ, S.AcidRate, 1.0, false), region);
            var matron = FillSettings.From(MatronSettings.Defaults.FillArgs(MatronSettings.Defaults.FillFloorZ, 0.03, 1.0), null);

            foreach (var depth in new[] { 1.0, 5.0 })
                Assert.AreEqual(FillRules.FlatDamage(depth, matron, 1.5), FillRules.FlatDamage(depth, harbinger, 1.5), 1e-9, $"depth {depth}");

            Assert.AreEqual(20.0 * 1.5, FillRules.FlatDamage(1.0, harbinger, 1.5), 1e-9, "1 m: the mild curve, 10 + 10 x 1");
            Assert.AreEqual(214.0 * 1.5, FillRules.FlatDamage(5.0, harbinger, 1.5), 1e-9, "5 m: the submerged tier, 139 + 25 x 3");
        }

        [TestMethod]
        public void Acid_FinalRun_StillValidates_WithTheDamageKeys()
        {
            Assert.IsTrue(HazardRegionPresets.TryGet("harbinger", 0, out var region));
            Assert.IsNull(FillRules.Validate(region, RunOf(HarbingerElement.Acid, true).Args));
            Assert.IsNull(FillRules.Validate(region, RunOf(HarbingerElement.Acid, false).Args));
        }
    }
}
