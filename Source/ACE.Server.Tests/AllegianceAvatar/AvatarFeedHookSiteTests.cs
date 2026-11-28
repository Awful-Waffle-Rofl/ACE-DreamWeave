using System;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Tests.ThreadDungeons;

namespace ACE.Server.Tests.Avatars
{
    /// <summary>
    /// The feed call must sit after the fellowship split (so it runs once per receiving member, on the member's own
    /// share), AFTER the offline bonus (owner ruling 2026-11-25: the offline bank's bonus XP counts toward Favor, for every
    /// player) and BEFORE the alt bonus and the Thread boon, which must not be counted. It is fed the offline-boosted
    /// levelAmount, never the raw amount.
    /// </summary>
    [TestClass]
    public class AvatarFeedHookSiteTests
    {
        private const string Call = "AvatarFeed.Capture(this, levelAmount, xpType);";

        private static string GrantXpBody() => PooledLootSourceText.MethodBody(PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Player_Xp.cs"),
            "public void GrantXP(long amount, XpType xpType, ShareType shareType, bool combatShare)");

        [TestMethod]
        public void Capture_IsCalledOnce_AfterTheSplitAndTheOfflineBonus_BeforeTheAltBonusAndTheBoon()
        {
            var body = GrantXpBody();

            var first = body.IndexOf(Call, StringComparison.Ordinal);
            Assert.IsTrue(first >= 0, "the Capture call is missing from GrantXP");
            Assert.AreEqual(-1, body.IndexOf(Call, first + 1, StringComparison.Ordinal), "the Capture call must appear exactly once");

            var split = body.IndexOf("Fellowship.SplitXp(", StringComparison.Ordinal);
            var levelAmount = body.IndexOf("var levelAmount = amount;", StringComparison.Ordinal);
            var offline = body.IndexOf("ApplyOfflineExperienceBonus(amount)", StringComparison.Ordinal);
            var alt = body.IndexOf("ApplyAltCharacterBonus(", StringComparison.Ordinal);
            var boon = body.IndexOf("ThreadBoons.ScaleAmount(", StringComparison.Ordinal);

            Assert.IsTrue(split >= 0 && levelAmount >= 0 && offline >= 0 && alt >= 0 && boon >= 0, "a landmark in GrantXP was not found");
            Assert.IsTrue(split < first, "Capture must come after Fellowship.SplitXp");
            Assert.IsTrue(levelAmount < first && offline < first, "Capture must come after the offline bonus, so offline-bank XP counts toward Favor");
            Assert.IsTrue(first < alt && first < boon, "Capture must come before the alt bonus and the Thread boon");
            Assert.AreEqual(-1, body.IndexOf("AvatarFeed.Capture(this, amount,", StringComparison.Ordinal), "the raw, un-boosted amount must not be fed");
        }

        [TestMethod]
        public void SplitXp_IsWrappedByEnterSplit_AndExitSplitInFinally()
        {
            var body = GrantXpBody();

            var split = body.IndexOf("Fellowship.SplitXp(", StringComparison.Ordinal);
            var enter = body.LastIndexOf("AvatarFeed.EnterSplit(this);", split, StringComparison.Ordinal);
            var tryAt = body.IndexOf("try", enter, StringComparison.Ordinal);
            var finallyAt = body.IndexOf("finally", split, StringComparison.Ordinal);
            var exit = body.IndexOf("AvatarFeed.ExitSplit(", finallyAt, StringComparison.Ordinal);
            var ret = body.IndexOf("return;", split, StringComparison.Ordinal);

            Assert.IsTrue(enter >= 0 && enter < tryAt && tryAt < split, "EnterSplit must precede a try that holds SplitXp");
            Assert.IsTrue(finallyAt > split && exit > finallyAt, "ExitSplit must sit in the finally after SplitXp");
            Assert.IsTrue(exit < ret, "ExitSplit must run before the branch returns");
        }
    }
}
