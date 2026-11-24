using System.Collections.Generic;

using ACE.Server.Avatars;

namespace ACE.Server.Tests.Avatars
{
    /// <summary>
    /// A synthetic XP chart for tests that read an Avatar's level. The production chart (EnlightenmentXpCurve) needs the
    /// client dat, so a test that reaches AvatarState.Level installs this through the AvatarCurves.ChartSource seam and
    /// resets it in cleanup (the seams are statics shared by every test class in the process).
    ///
    /// The totals follow the retired geometric curve (base 1000, growth 1.6): level L needs 1000 * (1.6^L - 1) / 0.6, so a
    /// pool of 10,000 is level 4 and 16,000 is level 5, and a pool of 0 is level 1 (the floor).
    /// </summary>
    internal static class AvatarTestChart
    {
        public static readonly IReadOnlyList<ulong> Geometric = Build();

        private static IReadOnlyList<ulong> Build()
        {
            var totals = new List<ulong> { 0 };
            double sum = 0, term = 1;
            for (var level = 1; level <= 60; level++)
            {
                sum += term;
                term *= 1.6;
                totals.Add((ulong)(1000 * sum + 1e-6));
            }
            return totals;
        }

        /// <summary>Installs the chart and pins the Gauntlet gate level the status line reads (the production read needs PropertyManager).</summary>
        public static void Install(long gauntletMinLevel = 1000)
        {
            AvatarCurves.ChartSource = () => Geometric;
            AvatarStatusText.GauntletMinLevel = () => gauntletMinLevel;
            AvatarLevelUp.GauntletMinLevel = () => gauntletMinLevel;
        }

        public static void Reset()
        {
            AvatarCurves.ResetSeams();
            AvatarStatusText.ResetSeams();
            AvatarLevelUp.ResetSeams();
        }
    }
}
