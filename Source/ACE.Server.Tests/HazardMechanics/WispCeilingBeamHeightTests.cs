using System;
using System.Numerics;
using System.Text.RegularExpressions;

using ACE.Server.HazardMechanics;
using ACE.Server.Pinnacles.Grael;
using ACE.Server.Tests.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.HazardMechanics
{
    /// <summary>
    /// The wisps' beam_height key (Harbinger round 3, owner 2026-11-19: "the laser beam indicator ... similar to Grael"). The
    /// ceiling beam's mesh hangs 5 m under its host (MemoryLayout.BeamOrientation, measured), and the host hangs 0.3 m under
    /// the region's ceiling, so in a room taller than about 5.3 m the beam ends in mid-air. beam_height places the host that
    /// far over the pillar's ground instead; its default (0) keeps the original placement, so Grael (the only other
    /// ceiling_beam user) is unchanged. The live adapter (WispSquadMechanic) needs a live context, so its wiring is pinned
    /// on source text. Reads no PropertyManager value.
    /// </summary>
    [TestClass]
    public class WispCeilingBeamHeightTests
    {
        /// <summary>The beam mesh's measured length under its host (MemoryLayout.BeamOrientation: vertices z -5.0 .. 0.0).</summary>
        private const float BeamLength = 5.0f;

        private static readonly Vector2 Xy = new Vector2(20f, -20f);

        [TestMethod]
        public void BeamHeight_DefaultsOff_ParsesAndClamps_AndForCycleKeepsIt()
        {
            Assert.AreEqual(0.0, WispSettings.From(HazardArgs.Empty).BeamHeight, "off by default");
            Assert.AreEqual(5.4, WispSettings.From(HazardArgs.Parse("beam_height=5.4")).BeamHeight, 1e-9);
            Assert.AreEqual(30.0, WispSettings.From(HazardArgs.Parse("beam_height=99")).BeamHeight, 1e-9, "clamped to 30");
            Assert.AreEqual(5.4, WispSettings.From(HazardArgs.Parse("beam_height=5.4 move_end=1 cycles=3")).ForCycle(2).BeamHeight, 1e-9);
        }

        [TestMethod]
        public void Validate_RefusesAnOutOfRangeOrNonNumericBeamHeight()
        {
            var region = HazardRegionPresets.Pinnacle(0);
            Func<string, string> validate = a => WispRules.Validate(region, HazardArgs.Merge(HazardArgs.Parse(a), HazardArgs.Parse(HazardDefaults.Wisps)), WispRules.Scatter);

            StringAssert.Contains(validate("beam_height=-1"), "beam_height");
            StringAssert.Contains(validate("beam_height=31"), "beam_height");
            StringAssert.Contains(validate("beam_height=x"), "beam_height");
            Assert.IsNull(validate("beam_height=0"));
            Assert.IsNull(validate("ceiling_beam=1.5 beam_height=5.4"));
        }

        [TestMethod]
        public void CeilingBeamHost_Off_IsTheOriginalUnderCeilingPoint()
        {
            foreach (var ground in new[] { -36f, float.NaN })
                Assert.AreEqual(MemoryLayout.CeilingBeamPoint(Xy, -11.7f), WispRules.CeilingBeamHost(Xy, -11.7f, ground, 0.0), $"ground {ground}");

            // a height but no readable ground keeps the original placement too
            Assert.AreEqual(MemoryLayout.CeilingBeamPoint(Xy, -11.7f), WispRules.CeilingBeamHost(Xy, -11.7f, float.NaN, 5.4));
        }

        [TestMethod]
        public void CeilingBeamHost_WithAHeight_PutsTheBeamsFootAtThePillar_InATallRoom()
        {
            // the Harbinger chamber: floor -36, ceiling face -11.7 (24.3 m): the original host ends the beam 19 m up
            var off = WispRules.CeilingBeamHost(Xy, -11.7f, -36f, 0.0);
            Assert.AreEqual(-12.0f, off.Z, 1e-4f);
            Assert.AreEqual(19.0f, off.Z - BeamLength - -36f, 1e-4f, "control: the original foot is 19 m over the floor");

            var on = WispRules.CeilingBeamHost(Xy, -11.7f, -36f, 5.4);
            Assert.AreEqual(Xy.X, on.X);
            Assert.AreEqual(Xy.Y, on.Y);
            Assert.AreEqual(-30.6f, on.Z, 1e-4f);
            Assert.AreEqual(0.4f, on.Z - BeamLength - -36f, 1e-4f, "the foot 0.4 m over the floor, as Grael's");

            // the Harbinger alcove: floor 0, ceiling 9.0: the original foot is 3.7 m up, the height brings it down
            Assert.AreEqual(3.7f, WispRules.CeilingBeamHost(Xy, 9f, 0f, 0.0).Z - BeamLength, 1e-4f);
            Assert.AreEqual(0.4f, WispRules.CeilingBeamHost(Xy, 9f, 0f, 5.4).Z - BeamLength, 1e-4f);
        }

        [TestMethod]
        public void CeilingBeamHost_NeverRisesAboveTheUnderCeilingPoint()
        {
            // Grael's room is 5.7 m tall: a 10 m height would put the host into the ceiling; it stays 0.3 m under it
            Assert.AreEqual(MemoryLayout.CeilingBeamPoint(Xy, -42.05f), WispRules.CeilingBeamHost(Xy, -42.05f, -47.75f, 10.0));
        }

        /// <summary>
        /// The Grael wisp rows are exactly origin/master's (golden strings copied from 2f23f28b3 GraelSettings.cs:68,124), with
        /// no beam_height, so the shared mechanic places their beam exactly where it did.
        /// </summary>
        [TestMethod]
        public void Grael_ArgsAreUnchanged_AndItsBeamHostIsTheOriginal()
        {
            Assert.AreEqual("pattern=scatter cycles=6 move=2.5 move_end=1.5 hold=1.5 hold_end=0.5 hold_floor=0 rest=1.0 rest_end=0.5 follow=1 follow_lag=2 mark_dest=1 ceiling_beam=1.5 mark_script=0x33000798 beam_script=0x33000798",
                GraelSettings.DefaultP2Wisps);
            Assert.AreEqual("pattern=scatter n=4 cycles=60 move=2.5 hold=1.5 rest=2.0 mark_dest=1 follow=0 ceiling_beam=1.5 mark_script=0x33000798 beam_script=0x33000798",
                GraelSettings.DefaultP5Wisps);

            var region = HazardRegionPresets.Pinnacle(0);

            foreach (var row in new[] { GraelSettings.DefaultP2Wisps, GraelSettings.DefaultP5Wisps })
            {
                var s = WispSettings.From(HazardArgs.Parse(row));
                Assert.AreEqual(0.0, s.BeamHeight, row);
                Assert.AreEqual(MemoryLayout.CeilingBeamPoint(Xy, region.CeilingZ.Value),
                    WispRules.CeilingBeamHost(Xy, region.CeilingZ.Value, region.GroundZ(Xy.X, Xy.Y), s.BeamHeight), row);
            }
        }

        /// <summary>
        /// The live adapter hands the run's beam_height and the pillar's ground to the pure placement. Pinned on source text
        /// (WispSquadMechanic's adapter needs a live HazardContext): the exact statements, whitespace collapsed.
        /// </summary>
        [TestMethod]
        public void LiveAdapter_SpawnCeilingBeam_PlacesTheHostThroughCeilingBeamHost_WithTheRunsBeamHeight()
        {
            var text = PooledLootSourceText.Read("Source/ACE.Server/HazardMechanics/WispSquadMechanic.cs");
            var body = Regex.Replace(PooledLootSourceText.MethodBody(text, "public HazardHandle SpawnCeilingBeam(Vector3 pillar, double seconds)"), @"\s+", " ");

            var ground = body.IndexOf("var groundZ = s.BeamHeight > 0.0 ? ctx.GroundZ(pillar.X, pillar.Y) : float.NaN;", StringComparison.Ordinal);
            var host = body.IndexOf("var host = WispRules.CeilingBeamHost(new Vector2(pillar.X, pillar.Y), ceilingZ.Value, groundZ, s.BeamHeight);", StringComparison.Ordinal);
            var spawn = body.IndexOf("return ctx.SpawnTimedEffect(host, s.BeamScript,", StringComparison.Ordinal);

            Assert.IsTrue(ground >= 0 && host > ground && spawn > host, $"ground={ground} host={host} spawn={spawn}");
        }
    }
}
