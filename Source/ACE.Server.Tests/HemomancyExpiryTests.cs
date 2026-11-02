using System.IO;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.ClassAbilities;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The "Hemomancy fades." one-shot: HemomancyState.TryConsumeExpiry on the pure state, plus a source
    /// pin that the heartbeat hook sends exactly that line through TryConsumeExpiry. No PropertyManager read.
    /// </summary>
    [TestClass]
    public class HemomancyExpiryTests
    {
        private static HemomancyArmResult Arm(HemomancyState state, double now)
            => state.OnHealthRestored(now, 100.0, false, 1000.0, 0.05, 2.0, 8.0);

        [TestMethod]
        public void NeverArmed_NeverExpires()
        {
            var state = new HemomancyState();

            Assert.IsFalse(state.TryConsumeExpiry(0.0));
            Assert.IsFalse(state.TryConsumeExpiry(1000.0));
        }

        [TestMethod]
        public void StillActive_DoesNotExpire()
        {
            var state = new HemomancyState();
            Arm(state, 100.0);

            Assert.IsFalse(state.TryConsumeExpiry(105.0));
            Assert.IsFalse(state.TryConsumeExpiry(108.0), "the expiry instant itself is still active");
            Assert.IsTrue(state.IsActive(108.0));
        }

        [TestMethod]
        public void Lapse_FiresExactlyOnce_ThenRearmingReadsAsFreshActivation()
        {
            var state = new HemomancyState();
            Arm(state, 100.0);

            Assert.IsTrue(state.TryConsumeExpiry(108.5), "first read past the expiry announces");
            Assert.IsFalse(state.TryConsumeExpiry(109.0), "and only once");

            Assert.AreEqual(HemomancyArmResult.Armed, Arm(state, 120.0), "a re-arm after the lapse is a new activation");
        }

        [TestMethod]
        public void Refresh_PushesTheExpiryOut_SoNoEarlyFade()
        {
            var state = new HemomancyState();
            Arm(state, 100.0);
            Assert.AreEqual(HemomancyArmResult.Refreshed, Arm(state, 106.0));

            Assert.IsFalse(state.TryConsumeExpiry(109.0));
            Assert.IsTrue(state.TryConsumeExpiry(114.5));
        }

        [TestMethod]
        public void Reset_SuppressesTheFadeLine()
        {
            var state = new HemomancyState();
            Arm(state, 100.0);
            state.Reset();

            Assert.IsFalse(state.TryConsumeExpiry(200.0), "a rank lost mid-buff drops the buff silently");
        }

        [TestMethod]
        public void HeartbeatHook_SendsTheFadeLine_ThroughTryConsumeExpiry()
        {
            var path = Path.Combine(ClassAbilityAffinityDeclarationTests.RepoRoot(), "Source", "ACE.Server",
                "WorldObjects", "Player_HemomancyTick.cs");
            var text = File.ReadAllText(path);

            StringAssert.Contains(text, "TryConsumeExpiry(");
            StringAssert.Contains(text, "SendClassAbilityBuffMessage(\"Hemomancy fades.\")");

            var buffs = File.ReadAllText(Path.Combine(ClassAbilityAffinityDeclarationTests.RepoRoot(), "Source",
                "ACE.Server", "WorldObjects", "Player_ClassAbilityBuffs.cs"));
            StringAssert.Contains(buffs, "TickHemomancyExpiry();");
        }
    }
}