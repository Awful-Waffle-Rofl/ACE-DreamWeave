using System.Text.RegularExpressions;

using ACE.Server.Tests.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.Avatars.Gauntlet
{
    /// <summary>
    /// Source pin for the owner ruling of 2026-11-23: /top gauntlet ignores the leaderboard exemption. The behaviour test
    /// (GauntletRunTests.ExemptParticipant_RunStillBanksAndAnnounces) cannot see an exemption seam wired only in
    /// LiveGauntletWorld.Build(), because the test Harness never sets one, so the live wiring files are scanned directly.
    /// </summary>
    [TestClass]
    public class GauntletExemptionSourcePinTests
    {
        private const string Token = "LeaderboardExemptionManager";

        private static readonly string[] Files =
        {
            "Source/ACE.Server/AllegianceAvatar/Gauntlet/LiveGauntletWorld.cs",
            "Source/ACE.Server/AllegianceAvatar/Gauntlet/GauntletRun.cs",
        };

        /// <summary>Null when the source never mentions the exemption manager, else the first fault.</summary>
        internal static string Fault(string source)
            => Regex.IsMatch(source, Regex.Escape(Token)) ? "references " + Token : null;

        [TestMethod]
        public void GauntletLiveWiringAndRun_NeverReferenceTheLeaderboardExemption()
        {
            foreach (var path in Files)
                Assert.IsNull(Fault(PooledLootSourceText.Read(path)), path);
        }

        [TestMethod]
        public void Pin_FaultsOnAnExemptionSeamWiredInTheLiveWorld()
        {
            Assert.IsNotNull(Fault("IsExemptContact = p => LeaderboardExemptionManager.IsExempt(p),"), "control: the checker detects the retired wiring");
            Assert.IsNull(Fault("MemberLookup = avatarId => p => AvatarRights.IsMemberForAdmission(p, AvatarManager.Get(avatarId)),"));
        }
    }
}
