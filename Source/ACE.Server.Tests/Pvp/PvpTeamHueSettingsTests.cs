using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Managers;
using ACE.Server.Pvp;
using ACE.Server.Pvp.Templates;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>
    /// The pvp_team_hue_* settings: the compiled defaults match the registered ones, the seams fall back when PropertyManager throws
    /// (as it does here, with no shard config), and one Snapshot reads each key once. Every seam a test swaps is put back in a finally.
    /// </summary>
    [TestClass]
    public class PvpTeamHueSettingsTests
    {
        private static void WithSeams(Action body)
        {
            var enabled = PvpTeamHueSettings.EnabledSource;
            var rp = PvpTeamHueSettings.RedPaletteSource;
            var rs = PvpTeamHueSettings.RedShadeSource;
            var bp = PvpTeamHueSettings.BluePaletteSource;
            var bs = PvpTeamHueSettings.BlueShadeSource;
            var np = PvpTeamHueSettings.NeutralPaletteSource;
            var ns = PvpTeamHueSettings.NeutralShadeSource;
            var ov = PvpTeamHueSettings.OverridesSource;
            var ffaPalettes = PvpTeamHueSettings.FfaPalettesSource;
            var ffaShade = PvpTeamHueSettings.FfaShadeSource;
            var sink = PvpTeamHueSettings.WarnSink;

            try
            {
                body();
            }
            finally
            {
                PvpTeamHueSettings.EnabledSource = enabled;
                PvpTeamHueSettings.RedPaletteSource = rp;
                PvpTeamHueSettings.RedShadeSource = rs;
                PvpTeamHueSettings.BluePaletteSource = bp;
                PvpTeamHueSettings.BlueShadeSource = bs;
                PvpTeamHueSettings.NeutralPaletteSource = np;
                PvpTeamHueSettings.NeutralShadeSource = ns;
                PvpTeamHueSettings.OverridesSource = ov;
                PvpTeamHueSettings.FfaPalettesSource = ffaPalettes;
                PvpTeamHueSettings.FfaShadeSource = ffaShade;
                PvpTeamHueSettings.WarnSink = sink;
            }
        }

        [TestMethod]
        public void CompiledDefaults_MatchTheRegisteredPropertyManagerDefaults()
        {
            Assert.AreEqual(PvpTeamHueSettings.DefaultEnabled, DefaultPropertyManager.DefaultBooleanProperties["pvp_team_hue_enabled"].Item);
            Assert.AreEqual(PvpTeamHueSettings.DefaultRedPalette, DefaultPropertyManager.DefaultLongProperties["pvp_team_hue_red_palette"].Item);
            Assert.AreEqual(PvpTeamHueSettings.DefaultBluePalette, DefaultPropertyManager.DefaultLongProperties["pvp_team_hue_blue_palette"].Item);
            Assert.AreEqual(PvpTeamHueSettings.DefaultNeutralPalette, DefaultPropertyManager.DefaultLongProperties["pvp_team_hue_neutral_palette"].Item);
            Assert.AreEqual(PvpTeamHueSettings.DefaultShade, DefaultPropertyManager.DefaultDoubleProperties["pvp_team_hue_red_shade"].Item);
            Assert.AreEqual(PvpTeamHueSettings.DefaultShade, DefaultPropertyManager.DefaultDoubleProperties["pvp_team_hue_blue_shade"].Item);
            Assert.AreEqual(PvpTeamHueSettings.DefaultShade, DefaultPropertyManager.DefaultDoubleProperties["pvp_team_hue_neutral_shade"].Item);
            Assert.AreEqual(PvpTeamHueSettings.DefaultOverrides, DefaultPropertyManager.DefaultStringProperties["pvp_team_hue_overrides"].Item);
            Assert.AreEqual(PvpTeamHueSettings.DefaultFfaPalettes, DefaultPropertyManager.DefaultStringProperties["pvp_team_hue_ffa_palettes"].Item);
            Assert.AreEqual(PvpTeamHueSettings.DefaultFfaShade, DefaultPropertyManager.DefaultDoubleProperties["pvp_team_hue_ffa_shade"].Item);
        }

        [TestMethod]
        public void FfaDefaults_AreTheOwnersPool_AndEveryDefaultEntryIsValid()
        {
            Assert.AreEqual("2,13,14,84,85,86,88,89,90", PvpTeamHueSettings.DefaultFfaPalettes);
            Assert.AreEqual(0.0, PvpTeamHueSettings.DefaultFfaShade);

            var warns = new List<string>();
            var pool = PvpTeamHue.ParseFfaPalettes(PvpTeamHueSettings.DefaultFfaPalettes, warns.Add);

            CollectionAssert.AreEqual(new[] { 2, 13, 14, 84, 85, 86, 88, 89, 90 }, pool.ToArray());
            Assert.AreEqual(0, warns.Count, "the shipped default parses clean");
        }

        [TestMethod]
        public void Snapshot_WithPropertyManagerThrowing_CarriesTheDefaultFfaPool()
        {
            // Nothing swapped: the real seams fall back to the compiled defaults, so the pool and shade are the shipped ones.
            var dials = PvpTeamHueSettings.Snapshot();

            CollectionAssert.AreEqual(new[] { 2, 13, 14, 84, 85, 86, 88, 89, 90 }, dials.FfaPalettes.ToArray());
            Assert.AreEqual(0.0, dials.FfaShade);
        }

        [TestMethod]
        public void Snapshot_ReadsTheFfaSeams_OncePerCall_ClampsTheShade_AndWarnsOnABadEntry()
        {
            WithSeams(() =>
            {
                var palettesReads = 0;
                var shadeReads = 0;
                var warns = new List<string>();

                PvpTeamHueSettings.WarnSink = warns.Add;
                PvpTeamHueSettings.FfaPalettesSource = () => { palettesReads++; return "14, 85,bad,85,0,2"; };
                PvpTeamHueSettings.FfaShadeSource = () => { shadeReads++; return 7.5; };

                var dials = PvpTeamHueSettings.Snapshot();

                CollectionAssert.AreEqual(new[] { 14, 85, 2 }, dials.FfaPalettes.ToArray());
                Assert.AreEqual(1.0, dials.FfaShade, "a shade above 1 is clamped, like every hue shade");
                Assert.AreEqual(1, palettesReads);
                Assert.AreEqual(1, shadeReads);
                Assert.AreEqual(3, warns.Count, "bad, the repeated 85 and 0 each warn once");
            });
        }

        [TestMethod]
        public void Snapshot_AThrowingFfaSeam_FallsBackToTheDefaultPool()
        {
            WithSeams(() =>
            {
                PvpTeamHueSettings.FfaPalettesSource = () => throw new InvalidOperationException("db");
                PvpTeamHueSettings.FfaShadeSource = () => throw new InvalidOperationException("db");

                var dials = PvpTeamHueSettings.Snapshot();

                Assert.AreEqual(9, dials.FfaPalettes.Count);
                Assert.AreEqual(0.0, dials.FfaShade);
            });
        }

        [TestMethod]
        public void Defaults_AreTheOwnersPlaceholders()
        {
            Assert.IsTrue(PvpTeamHueSettings.DefaultEnabled, "new settings default ON");
            Assert.AreEqual(85, PvpTeamHueSettings.DefaultRedPalette);
            Assert.AreEqual(88, PvpTeamHueSettings.DefaultBluePalette);
            Assert.AreEqual(90, PvpTeamHueSettings.DefaultNeutralPalette);
        }

        [TestMethod]
        public void Snapshot_WithPropertyManagerThrowing_FallsBackToTheCompiledDefaults()
        {
            // Nothing is swapped: the real seams read PropertyManager, which throws here (no shard config), so every key falls back.
            var dials = PvpTeamHueSettings.Snapshot();

            Assert.IsTrue(dials.Enabled);
            Assert.AreEqual(new PvpTeamHueLook(85, 0.0), dials.Red);
            Assert.AreEqual(new PvpTeamHueLook(88, 0.0), dials.Blue);
            Assert.AreEqual(new PvpTeamHueLook(90, 0.0), dials.Neutral);
            Assert.AreEqual(0, dials.Overrides.Count);
        }

        [TestMethod]
        public void Snapshot_ReadsEverySeam_OncePerCall()
        {
            WithSeams(() =>
            {
                var reads = new Dictionary<string, int>();

                T Count<T>(string name, T value)
                {
                    reads[name] = reads.TryGetValue(name, out var n) ? n + 1 : 1;
                    return value;
                }

                PvpTeamHueSettings.EnabledSource = () => Count("enabled", false);
                PvpTeamHueSettings.RedPaletteSource = () => Count("rp", 14L);
                PvpTeamHueSettings.RedShadeSource = () => Count("rs", 0.1);
                PvpTeamHueSettings.BluePaletteSource = () => Count("bp", 2L);
                PvpTeamHueSettings.BlueShadeSource = () => Count("bs", 0.2);
                PvpTeamHueSettings.NeutralPaletteSource = () => Count("np", 39L);
                PvpTeamHueSettings.NeutralShadeSource = () => Count("ns", 0.3);
                PvpTeamHueSettings.OverridesSource = () => Count("ov", "1000018E:red=85/0.4");

                var dials = PvpTeamHueSettings.Snapshot();

                Assert.IsFalse(dials.Enabled);
                Assert.AreEqual(new PvpTeamHueLook(14, 0.1), dials.Red);
                Assert.AreEqual(new PvpTeamHueLook(2, 0.2), dials.Blue);
                Assert.AreEqual(new PvpTeamHueLook(39, 0.3), dials.Neutral);
                Assert.AreEqual(new PvpTeamHueLook(85, 0.4), dials.Overrides[(0x1000018Eu, KitHue.Red)]);
                Assert.AreEqual(8, reads.Count);

                foreach (var kv in reads)
                    Assert.AreEqual(1, kv.Value, $"{kv.Key} is read once per snapshot");
            });
        }

        [TestMethod]
        public void Snapshot_TheDialsAreImmutable_ALaterSettingChangeDoesNotReachAnEarlierSnapshot()
        {
            WithSeams(() =>
            {
                PvpTeamHueSettings.RedPaletteSource = () => 14L;
                var first = PvpTeamHueSettings.Snapshot();

                PvpTeamHueSettings.RedPaletteSource = () => 2L;
                var second = PvpTeamHueSettings.Snapshot();

                Assert.AreEqual(14, first.Red.Palette);
                Assert.AreEqual(2, second.Red.Palette);
            });
        }

        [TestMethod]
        public void Snapshot_AThrowingSeam_FallsBackForThatKeyOnly()
        {
            WithSeams(() =>
            {
                PvpTeamHueSettings.RedPaletteSource = () => throw new InvalidOperationException("db");
                PvpTeamHueSettings.BluePaletteSource = () => 7L;

                var dials = PvpTeamHueSettings.Snapshot();

                Assert.AreEqual(85, dials.Red.Palette);
                Assert.AreEqual(7, dials.Blue.Palette);
            });
        }

        [TestMethod]
        public void Look_ClampsShade_AndTreatsAnOutOfRangePaletteAsKeepOwn()
        {
            Assert.AreEqual(new PvpTeamHueLook(0, 0.0), PvpTeamHueSettings.Look(-1, double.NaN));
            Assert.AreEqual(new PvpTeamHueLook(0, 1.0), PvpTeamHueSettings.Look(long.MaxValue, 5.0));
            Assert.AreEqual(new PvpTeamHueLook(5, 0.0), PvpTeamHueSettings.Look(5, -3.0));
            Assert.AreEqual(new PvpTeamHueLook(5, 0.25), PvpTeamHueSettings.Look(5, 0.25));
            Assert.AreEqual(new PvpTeamHueLook(0, 0.5), PvpTeamHueSettings.Look(0, 0.5));
        }

        [TestMethod]
        public void Snapshot_ABadOverrideEntry_IsReportedThroughTheWarnSink_AndSkipped()
        {
            WithSeams(() =>
            {
                var warns = new List<string>();
                PvpTeamHueSettings.WarnSink = warns.Add;
                PvpTeamHueSettings.OverridesSource = () => "garbage;1000018E:blue=88/0.6";

                var dials = PvpTeamHueSettings.Snapshot();

                Assert.AreEqual(1, warns.Count);
                StringAssert.Contains(warns[0], "garbage");
                Assert.AreEqual(1, dials.Overrides.Count);
                Assert.AreEqual(new PvpTeamHueLook(88, 0.6), dials.Overrides[(0x1000018Eu, KitHue.Blue)]);
            });
        }
    }
}
