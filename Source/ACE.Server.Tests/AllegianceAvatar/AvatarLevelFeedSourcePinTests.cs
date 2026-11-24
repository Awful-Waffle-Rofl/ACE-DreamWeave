using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Server.Avatars;
using ACE.Server.Tests.ThreadDungeons;

namespace ACE.Server.Tests.Avatars
{
    /// <summary>
    /// The level pool hook's ORDER and PRESENCE, pinned against the source text. A seeded Player ends GrantXP in EnqueueAction
    /// (it has no action queue), so UpdateXpAndLevel never runs under this harness; and a private event or a Gauntlet copy needs
    /// a live world. What must hold is structural: the call receives the amount AFTER the level-cap clamp, and CaptureLevel
    /// applies none of the Favor feed's skips. Each pin is mutation-proved below.
    /// </summary>
    [TestClass]
    public class AvatarLevelFeedSourcePinTests
    {
        private const string XpPath = "Source/ACE.Server/WorldObjects/Player_Xp.cs";
        private const string FeedPath = "Source/ACE.Server/AllegianceAvatar/AvatarFeed.cs";

        private const string UpdateSig = "private void UpdateXpAndLevel(long amount, XpType xpType)";
        private const string CaptureSig = "public static void CaptureLevel(Player player, long added, XpType xpType)";
        private const string Call = "AvatarFeed.CaptureLevel(this, addAmount, xpType);";
        private const string Clamp = "addAmount = amountLeftToEnd;";
        private const string TotalUpdate = "TotalExperience += addAmount;";
        private const string LevelCheck = "CheckForLevelup();";

        private static string Norm(string s) => s.Replace("\r\n", "\n");

        /// <summary>The call is in UpdateXpAndLevel exactly once, after the clamp and the TotalExperience update, before the level check, and nowhere else in the file.</summary>
        internal static string PlayerXpFault(string source)
        {
            if (source.Split(new[] { "AvatarFeed.CaptureLevel(" }, StringSplitOptions.None).Length - 1 != 1)
                return "AvatarFeed.CaptureLevel must be called exactly once in Player_Xp.cs";

            string body;
            try
            {
                body = PooledLootSourceText.MethodBody(source, UpdateSig);
            }
            catch (Exception e)
            {
                return "UpdateXpAndLevel not found: " + e.Message;
            }

            var call = body.IndexOf(Call, StringComparison.Ordinal);
            var clamp = body.IndexOf(Clamp, StringComparison.Ordinal);
            var total = body.IndexOf(TotalUpdate, StringComparison.Ordinal);
            var check = body.IndexOf(LevelCheck, StringComparison.Ordinal);

            if (call < 0)
                return "UpdateXpAndLevel must call " + Call + " (the post-clamp amount, the member's own type)";
            if (clamp < 0 || total < 0 || check < 0)
                return "the clamp, the TotalExperience update or the level check moved: re-derive this pin";
            if (!(clamp < total && total < call && call < check))
                return "the call must sit after the clamp and the TotalExperience update, before CheckForLevelup";

            return null;
        }

        /// <summary>CaptureLevel hands the received amount to LevelSink and applies none of the Favor feed's skips, caps or multipliers.</summary>
        internal static string CaptureLevelFault(string source)
        {
            string body;
            try
            {
                body = PooledLootSourceText.MethodBody(source, CaptureSig);
            }
            catch (Exception e)
            {
                return "CaptureLevel not found: " + e.Message;
            }

            if (!body.Contains("LevelSink(monarch.Value, added)"))
                return "CaptureLevel must enqueue the received amount through LevelSink";

            foreach (var forbidden in new[] { "WorldEventHooks", "PrivateEventApplies", "GauntletCopyApplies", "InGauntletCopy", "ReceivedAfterCap", "CeilingSource", "AvatarFeedRules", "Multiplier" })
            {
                if (body.Contains(forbidden))
                    return "CaptureLevel must not use " + forbidden + ": private events and Gauntlet copies feed the pool too";
            }

            return null;
        }

        [TestMethod]
        public void UpdateXpAndLevel_PassesThePostClampAmount_ToTheLevelFeed() => Assert.IsNull(PlayerXpFault(Norm(PooledLootSourceText.Read(XpPath))));

        [TestMethod]
        public void CaptureLevel_AppliesNoPrivateEventOrGauntletSkip() => Assert.IsNull(CaptureLevelFault(Norm(PooledLootSourceText.Read(FeedPath))));

        [TestMethod]
        public void PlayerXpPin_FaultsOnMutations()
        {
            var src = Norm(PooledLootSourceText.Read(XpPath));
            string Mutate(string from, string to)
            {
                var result = src.Replace(from, to);
                Assert.AreNotEqual(src, result, "mutation did not apply");
                return result;
            }

            Assert.IsNotNull(PlayerXpFault(Mutate(Call, "")), "call removed");
            Assert.IsNotNull(PlayerXpFault(Mutate(Call, "AvatarFeed.CaptureLevel(this, amount, xpType);")), "raw amount instead of the clamped one");
            Assert.IsNotNull(PlayerXpFault(Mutate(Call, Call + "\n" + Call)), "called twice");

            // moved before the clamp
            var noCall = Mutate(Call, "");
            var at = noCall.IndexOf("var addAmount = amount;", StringComparison.Ordinal);
            Assert.IsTrue(at >= 0);
            var early = noCall.Insert(at, Call + "\n");
            Assert.IsNotNull(PlayerXpFault(early), "moved before the clamp");
        }

        [TestMethod]
        public void CaptureLevelPin_FaultsOnMutations()
        {
            var src = Norm(PooledLootSourceText.Read(FeedPath));
            string Mutate(string from, string to)
            {
                var result = src.Replace(from, to);
                Assert.AreNotEqual(src, result, "mutation did not apply");
                return result;
            }

            Assert.IsNotNull(CaptureLevelFault(Mutate("LevelSink(monarch.Value, added)", "LevelSink(monarch.Value, 0)")), "sink call changed");
            Assert.IsNotNull(CaptureLevelFault(Mutate("            var monarch = ResolveMonarch(player);\n            if (monarch == null)\n                return;\n\n            LevelSink",
                "            var monarch = ResolveMonarch(player);\n            if (monarch == null || WorldEventHooks.AvatarFeedState(player.Guid.Full).PrivateAvatarEvent)\n                return;\n\n            LevelSink")), "a private event skip added");
            Assert.IsNotNull(CaptureLevelFault(Mutate("            var monarch = ResolveMonarch(player);\n            if (monarch == null)\n                return;\n\n            LevelSink",
                "            var monarch = ResolveMonarch(player);\n            if (monarch == null || GauntletHooks.InGauntletCopy(player))\n                return;\n\n            LevelSink")), "a Gauntlet copy skip added");
        }
    }
}
