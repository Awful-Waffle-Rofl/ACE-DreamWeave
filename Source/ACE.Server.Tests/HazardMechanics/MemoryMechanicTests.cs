using System.Linq;
using System.Numerics;

using ACE.Server.HazardMechanics;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.HazardMechanics
{
    /// <summary>The memory mechanic's pre-flight (Validate, RequiredWcids), its registration and its constants. No world.</summary>
    [TestClass]
    public class MemoryMechanicTests
    {
        private static HazardRegion Rect(float size = 40f, params HazardExclusion[] exclusions)
            => HazardRegion.Rect("rect", 0x0069, 0, false, 0, size, 0, size, HazardGroundMode.Plane, 0, null, 0, 10f, 2, 6, exclusions);

        private static string Validate(HazardRegion region, string args = "")
            => new MemoryMechanic().Validate(region, HazardArgs.Merge(HazardArgs.Parse(args), HazardArgs.Parse(HazardDefaults.Memory)), null);

        [TestMethod]
        public void TheRegistry_HandsOutAFreshMemoryMechanic_NotTheStub()
        {
            Assert.IsTrue(HazardMechanicRegistry.TryCreate("memory", out var a));
            Assert.IsTrue(HazardMechanicRegistry.TryCreate("MEMORY", out var b));

            Assert.IsInstanceOfType(a, typeof(MemoryMechanic));
            Assert.AreNotSame(a, b);
            Assert.AreEqual("memory", a.Kind);
            Assert.AreEqual(0, a.Patterns.Count);

            // the other three are still the stubs - this change replaced exactly one line
            foreach (var kind in new[] { "wisps", "balls", "beams" })
            {
                HazardMechanicRegistry.TryCreate(kind, out var other);
                Assert.IsNotInstanceOfType(other, typeof(MemoryMechanic), kind);
            }
        }

        [TestMethod]
        public void RequiredWcids_AreTheFourHillMarkers_PlusTheEffectHostTheBeamsUse()
        {
            var wcids = new MemoryMechanic().RequiredWcids(HazardArgs.Empty).ToList();

            foreach (var hill in new uint[] { 1006800, 1006801, 1006802, 1006803 })
                CollectionAssert.Contains(wcids, hill);

            CollectionAssert.Contains(wcids, HazardDefaults.EffectHostWcid, "SpawnTimedEffect spawns the Effect Host");
            Assert.AreEqual(5, wcids.Count);
        }

        [TestMethod]
        public void TheConstants_MatchTheOwnerDesign()
        {
            CollectionAssert.AreEqual(new uint[] { 1006800, 1006801, 1006802, 1006803 }, MemoryRules.PadWcids.ToArray());
            CollectionAssert.AreEqual(new uint[] { 0x33000791, 0x33000794, 0x33000792, 0x33000793 }, MemoryRules.BeamScripts.ToArray());
            CollectionAssert.AreEqual(new[] { "red", "blue", "green", "purple" }, MemoryRules.ColourNames.ToArray());
            Assert.AreEqual(HazardDefaults.BeamScript, MemoryRules.BeamScripts[0]);
            Assert.AreEqual(4, MemoryRules.BeamScripts.Distinct().Count());
        }

        [TestMethod]
        public void Validate_AcceptsEveryKindOfRegionWithTheShippedSettings()
        {
            Assert.IsNull(Validate(Rect()));
            Assert.IsNull(Validate(HazardRegion.Circle("c", 0x0069, 0, false, 100, 100, 20, HazardGroundMode.Plane, 0, null, 0, 10f, 2, 6)));

            var adhoc = HazardRegion.AdHoc(0x0069, 0, false, new Vector3(50, 50, 0), HazardShapeSpec.Circle(15), 0, out _);
            StringAssert.Contains(Validate(adhoc), "ceiling", "an ad-hoc region has no ceiling: the default ceiling beams are refused in one line");
            Assert.IsNull(Validate(adhoc, "beam_from=floor"));

            var outdoor = HazardRegion.AdHoc(0x0069, 0, true, new Vector3(96, 96, 0), HazardShapeSpec.Rect(30, 30), 0, out _);
            Assert.IsNull(Validate(outdoor, "beam_from=floor"));
        }

        [TestMethod]
        public void Validate_RefusesCeilingBeamsWithoutACeiling_InOneLine_AndATypoInTheBeamArgs()
        {
            var noCeiling = HazardRegion.Rect("rect", 0x0069, 0, false, 0, 40, 0, 40, HazardGroundMode.Plane, 0, null, 0, null, 2, 6);

            var error = Validate(noCeiling);
            StringAssert.Contains(error, "beam_from=ceiling");
            StringAssert.Contains(error, "beam_from=floor");
            Assert.IsFalse(error.Contains((char)10));

            StringAssert.Contains(Validate(noCeiling, "beam_from=ceiling"), "needs a region with a ceiling");
            Assert.IsNull(Validate(noCeiling, "beam_from=floor"), "floor beams need no ceiling");
            Assert.IsNull(Validate(Rect()), "positive control: a region with a ceiling takes the default");

            StringAssert.Contains(Validate(Rect(), "beam_from=sky"), "beam_from");
            StringAssert.Contains(Validate(Rect(), "beam_flip=maybe"), "beam_flip");
            Assert.IsNull(Validate(Rect(), "beam_flip=0"));
            Assert.IsNull(Validate(Rect(), "beam_flip=1 beam_from=CEILING"));
            StringAssert.Contains(Validate(Rect(), "beam_corners=maybe"), "beam_corners");
            Assert.IsNull(Validate(Rect(), "beam_corners=1 beams_per_pulse=12"));

            // corners exist only on a Rect: a circle refuses beam_corners=1 in one line, and takes beam_corners=0
            var circle = HazardRegion.Circle("c", 0x0069, 0, false, 100, 100, 20, HazardGroundMode.Plane, 0, null, 0, 10f, 2, 6);
            var refusal = Validate(circle, "beam_corners=1");
            StringAssert.Contains(refusal, "beam_corners=1 needs a rectangular region");
            Assert.IsFalse(refusal.Contains((char)10));
            Assert.IsNull(Validate(circle, "beam_corners=0"), "positive control: the same circle without corners");
        }

        [TestMethod]
        public void Validate_RefusesARegionTooSmallForFourPads()
        {
            StringAssert.Contains(Validate(Rect(8f)), "too small");
            StringAssert.Contains(Validate(HazardRegion.Circle("c", 0x0069, 0, false, 100, 100, 6, HazardGroundMode.Plane, 0, null, 0, 10f, 2, 6)), "too small");
        }

        [TestMethod]
        public void Validate_RefusesAPadGapTheRegionCannotHonour()
        {
            Assert.IsNull(Validate(Rect(20f), "pad_gap=10"));
            Assert.IsNotNull(Validate(Rect(20f), "pad_gap=11"));
        }

        [TestMethod]
        public void Validate_RefusesPadsThatWouldTouch_AndALenOverMaxLen()
        {
            StringAssert.Contains(Validate(Rect(), "pad_radius=3"), "pad_gap");
            Assert.IsNull(Validate(Rect(), "pad_radius=3 pad_gap=8"));

            StringAssert.Contains(Validate(Rect(), "len=10 max_len=6"), "max_len");
            Assert.IsNull(Validate(Rect(), "len=6 max_len=6"));
        }

        [TestMethod]
        public void Validate_RefusesAnExcludedPad_UnlessLayoutIsRandom()
        {
            var region = Rect(40f, HazardExclusion.Circle(10, 10, 3));

            StringAssert.Contains(Validate(region), "does not fit");
            Assert.IsNull(Validate(region, "layout=random"));
        }

        [TestMethod]
        public void Validate_UsesTheFrameworkEdgeInset()
        {
            // an 11 m square: quarter centres are 2.75 m from the edge, a pad needs radius 1 + inset 2 = 3 m; the centres are 5.5 m apart
            StringAssert.Contains(Validate(Rect(11f), "pad_gap=5"), "does not fit");
            Assert.IsNull(Validate(Rect(11f), "pad_gap=5 edge_inset=0"), "with no inset the pads fit");
            Assert.IsNull(Validate(Rect(11f), "pad_gap=5 edge_inset=1.5"), "a smaller inset fits too");
        }
        [TestMethod]
        public void DescribeAndReveal_BeforeStart_AreSafe()
        {
            var m = new MemoryMechanic();

            Assert.IsNull(m.Reveal(null));
            Assert.AreEqual("not started", m.Describe(null));
        }

        [TestMethod]
        public void PadPillar_DefaultOff_ReadsOne_AndRefusesANonBoolean()
        {
            Assert.IsFalse(MemorySettings.Read(HazardArgs.Empty).PadPillar);
            Assert.IsFalse(MemorySettings.Read(HazardArgs.Parse(HazardDefaults.Memory)).PadPillar, "the shipped default row does not turn it on");
            Assert.IsTrue(MemorySettings.Read(HazardArgs.Parse("pad_pillar=1")).PadPillar);
            Assert.IsFalse(MemorySettings.Read(HazardArgs.Parse("pad_pillar=0")).PadPillar);

            StringAssert.Contains(Validate(Rect(), "pad_pillar=x"), "pad_pillar");
            StringAssert.Contains(Validate(Rect(), "pad_pillar=x"), "1|0");
            Assert.IsNull(Validate(Rect(), "pad_pillar=1"), "positive control: a good value validates");
            Assert.IsNull(Validate(Rect(), "pad_pillar=0"));
        }

        [TestMethod]
        public void Pct_IsRefusedWhenNotANumber_AndAcceptedWhenOne()
        {
            StringAssert.Contains(Validate(Rect(), "pct=lots"), "pct");
            StringAssert.Contains(Validate(Rect(), "pct=lots"), "not a number");
            Assert.IsNull(Validate(Rect(), "pct=0.4"), "positive control: a good value validates");
        }

        [TestMethod]
        public void TheRetiredPadBeamsKey_IsIgnored_NotRefused_SoAStaleShardRowStillStarts()
        {
            // pad_beams was retired for pad_pillar: an unknown key is neither read nor refused (no unknown-key validation exists)
            Assert.IsNull(Validate(Rect(), "pad_beams=2"));
            Assert.IsNull(Validate(Rect(), "pad_beams=x"), "not even validated as a number any more");
            Assert.IsFalse(MemorySettings.Read(HazardArgs.Parse("pad_beams=2")).PadPillar, "and it never turns the pillar on");
        }
    }
}
