using System;
using System.Linq;

using ACE.Server.Frays;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// No elites (Gilded rich points cover the high-value role), lower Ember Shrine
    /// buffs, and no Fray Effigy from the Great Snarl.
    /// </summary>
    [TestClass]
    public class FraysNoElitesTests
    {
        [TestMethod]
        public void Elites_AndOtherDeadSettings_AreGone()
        {
            foreach (var dead in new[] { "elites_enabled", "elite_chance", "cache_items_hard", "snarl_health_hard", "patterns_enabled", "pattern_perks", "xp_per_hour", "lum_per_hour", "auto_hold_minutes", "gate_every", "shrine_on_gilded" })
                Assert.IsFalse(FraysSettings.TryFind(dead, out _), dead + " should be gone from the admin page");
        }

        [TestMethod]
        public void Nests_HaveNoElite()
        {
            foreach (FraysPointKind kind in Enum.GetValues(typeof(FraysPointKind)))
                foreach (FraysShape shape in Enum.GetValues(typeof(FraysShape)))
                    for (var extra = 0; extra < 4; extra++)
                    {
                        var roles = FraysRules.PackTemplate(kind, 3, shape, extra, true);
                        Assert.IsFalse(roles.Contains(FraysRole.Elite), $"{kind} {shape} +{extra}");
                    }

            Assert.IsFalse(FraysRules.PackName(FraysPointKind.Nest, 0, FraysShape.Medium).Contains("elite"));
        }

        [TestMethod]
        public void EliteTexts_NowNameGilded()
        {
            Assert.IsTrue(FraysBoons.Find("huntercoal").Text.Contains("Gilded"));
            Assert.IsTrue(FraysWards.WeekText(1).Contains("Gilded"));

            foreach (var shift in new[] { FraysAffix.UnspooledSwarm, FraysAffix.TangledKings, FraysAffix.FrayedMasses })
                Assert.IsFalse(FraysWeek.Effect(shift).Contains("elite"), shift.ToString());

            foreach (var shift in new[] { FraysAffix.Champions, FraysAffix.Tyrannical })
                Assert.IsFalse(FraysHeatShifts.Effect(shift).Contains("elite"), shift.ToString());
        }

        [TestMethod]
        public void HeatMilestone_NeverPromisesElites()
        {
            for (var level = 0; level < 25; level++)
                Assert.IsFalse(FraysRules.NextMilestone(level, 25, 5).Contains("elite"));
        }

        [TestMethod]
        public void ShrineBuffs_AreTheLowerNumbers()
        {
            Assert.AreEqual(0.10, FraysSettings.Number(FraysSettings.ShrineFury), 1e-9);   // every week, lasting as long as its cooldown
            Assert.AreEqual(0.05, FraysSettings.Number(FraysSettings.ShrineWard), 1e-9);
            Assert.AreEqual(0.25, FraysSettings.Number(FraysSettings.ShrineStoke), 1e-9);
            Assert.AreEqual(0.01, FraysSettings.Number(FraysSettings.ShrineMending), 1e-9);
        }

        [TestMethod]
        public void GreatSnarl_GivesNoEffigy()
        {
            var src = System.IO.File.ReadAllText(SourcePath("ACE.Server", "Frays", "FraysFrayWide.cs"));
            Assert.IsFalse(src.Contains("FraysEffigies.Give("), "the Great Snarl must not give a Fray Effigy");
        }

        private static string SourcePath(params string[] parts)
        {
            var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !System.IO.Directory.Exists(System.IO.Path.Combine(dir.FullName, "ACE.Server")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, "could not find the Source folder");
            return System.IO.Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
        }
    }
}
