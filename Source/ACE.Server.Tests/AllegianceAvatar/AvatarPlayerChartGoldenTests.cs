using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.DatLoader;
using ACE.Server.Avatars;
using ACE.Server.Entity;

namespace ACE.Server.Tests.Avatars
{
    /// <summary>
    /// Golden pins of the Avatar level against the REAL player chart (EnlightenmentXpCurve.ExtendedTotals, read from the
    /// client dat): the Avatar levels like a normal character, so a pool total maps to the level a player holding that much
    /// total XP has. Every pin sits on a chart boundary (the last XP of one level and the first of the next), so an
    /// off-by-one in the search (the highest L with chart[L] less than, rather than at most, the pool) goes red.
    ///
    /// The pins come from the dat, not from the code under test. Skipped (Inconclusive) without a client dat: set ACE_DAT_PATH.
    /// </summary>
    [TestClass]
    public class AvatarPlayerChartGoldenTests
    {
        private static readonly object initLock = new object();
        private static bool initialized;
        private static string skipReason;

        [TestInitialize]
        public void Setup() => AvatarCurves.ResetSeams();

        private static void RequireDats()
        {
            lock (initLock)
            {
                if (!initialized)
                {
                    initialized = true;
                    skipReason = InitializeDats();
                }
            }

            if (skipReason != null)
                Assert.Inconclusive(skipReason);
        }

        private static string InitializeDats()
        {
            if (DatManager.PortalDat != null)
                return null; // already loaded by something else in this test host

            var candidates = new List<string>();

            var fromEnv = Environment.GetEnvironmentVariable("ACE_DAT_PATH");
            if (!string.IsNullOrWhiteSpace(fromEnv))
                candidates.Add(fromEnv);

            candidates.Add(@"C:\ACE\Dats\");
            candidates.Add(@"C:\Turbine\Asheron's Call\");

            var datDir = candidates.FirstOrDefault(c => File.Exists(Path.Combine(c, "client_portal.dat")));

            if (datDir == null)
                return $"Skipped: client_portal.dat not found in any of [{string.Join(", ", candidates)}]. Set ACE_DAT_PATH to a directory containing the client .dat files.";

            try
            {
                System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
                DatManager.Initialize(datDir, true, false);
            }
            catch (Exception ex)
            {
                return $"Skipped: DatManager.Initialize({datDir}) failed: {ex.Message}";
            }

            return DatManager.PortalDat == null ? $"Skipped: DatManager.Initialize({datDir}) did not produce a PortalDat." : null;
        }

        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void LevelForPool_PlayerChart_BoundaryPins()
        {
            RequireDats();

            var pins = new (ulong Pool, int Level)[]
            {
                (0UL, 1),
                (5_834_899UL, 29),
                (5_834_900UL, 30),
                (1_418_089_760UL, 99),
                (1_418_089_761UL, 100),
                (191_226_310_246UL, 274),
                (191_226_310_247UL, 275),
                (ulong.MaxValue, 1445),
            };

            foreach (var (pool, level) in pins)
                Assert.AreEqual(level, AvatarCurves.LevelForPool(pool, AvatarCurves.ChartSource()), $"pool {pool}");
        }

        /// <summary>The production seam IS the chart players use, and an Avatar state reads its level through it.</summary>
        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void ChartSource_Default_IsThePlayerChart_AndAvatarStateReadsIt()
        {
            RequireDats();

            Assert.AreSame(EnlightenmentXpCurve.ExtendedTotals, AvatarCurves.ChartSource());

            var state = new AvatarState { LevelXp = 5_834_900UL };
            Assert.AreEqual(30, state.Level(AvatarTuning.Defaults));
        }
    }
}
