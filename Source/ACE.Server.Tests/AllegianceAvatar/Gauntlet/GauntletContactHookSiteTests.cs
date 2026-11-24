using System;
using System.Text.RegularExpressions;

using ACE.Server.Tests.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.Avatars.Gauntlet
{
    /// <summary>
    /// Source pins for the Gauntlet contact wiring (plan Task 11): GauntletHooks.OnContact is the FIRST statement of both World
    /// Event entry points in WorldEventHooks.cs (so it receives every call site those already receive), and the Avatar feed
    /// returns on a Gauntlet copy through the exact if-head, after the private-event check and before the classify call. Each
    /// pin is a checker returning the first fault, run over the real source (null expected) and over mutated copies (each must
    /// fault), so every pin is mutation-proved inside the test.
    /// </summary>
    [TestClass]
    public class GauntletContactHookSiteTests
    {
        private const string HooksPath = "Source/ACE.Server/WorldEvents/WorldEventHooks.cs";
        private const string FeedPath = "Source/ACE.Server/AllegianceAvatar/AvatarFeed.cs";

        private const string DamageSig = "public static void OnDamageRecorded(Creature victim, WorldObject attacker, uint amount)";
        private const string ContactSig = "public static void OnCombatContact(Creature victim, WorldObject attacker)";
        private const string GauntletCall = "ACE.Server.Avatars.Gauntlet.GauntletHooks.OnContact(victim, attacker);";
        private const string DamageWe = "WorldEventCombatCreditHook.OnDamageRecorded(victim, attacker, amount);";
        private const string ContactWe = "WorldEventCombatCreditHook.OnCombatContact(victim, attacker);";

        internal const string FeedHead = "if (GauntletCopyApplies(GauntletHooks.InGauntletCopy(player), guid, splitEarner?.Guid.Full ?? 0, _ => GauntletHooks.InGauntletCopy(splitEarner)))\n                return;";
        private const string PrivateHead = "if (PrivateEventApplies(eventState, guid, splitEarner?.Guid.Full ?? 0, WorldEventHooks.AvatarFeedState))";
        private const string ClassifyCall = "AvatarFeedRules.Classify(";

        private static string Norm(string s) => s.Replace("\r\n", "\n");

        private static int Count(string text, string needle) => Regex.Matches(text, Regex.Escape(needle)).Count;

        /// <summary>The body starts with the Gauntlet call, then the World Event call, and the Gauntlet call appears nowhere else in the file.</summary>
        internal static string EntryFault(string source, string signature, string weCall)
        {
            if (Count(source, GauntletCall) != 2)
                return "GauntletHooks.OnContact must be called exactly twice in WorldEventHooks.cs (once per entry point)";

            string body;
            try
            {
                body = PooledLootSourceText.MethodBody(source, signature);
            }
            catch (Exception e)
            {
                return "entry point not brace-bodied or not found: " + e.Message;
            }

            var trimmed = body.Trim().TrimStart('{').TrimStart();

            if (!trimmed.StartsWith(GauntletCall + "\n", StringComparison.Ordinal))
                return signature + ": the first statement is not the Gauntlet call";

            var rest = trimmed.Substring(GauntletCall.Length).TrimStart();

            if (!rest.StartsWith(weCall, StringComparison.Ordinal))
                return signature + ": the World Event call does not follow the Gauntlet call";

            return null;
        }

        internal static string FeedFault(string source)
        {
            if (Count(source, FeedHead) != 1)
                return "the Gauntlet copy return is not in AvatarFeed.Capture exactly once";

            var priv = source.IndexOf(PrivateHead, StringComparison.Ordinal);
            var head = source.IndexOf(FeedHead, StringComparison.Ordinal);
            var classify = source.IndexOf(ClassifyCall, StringComparison.Ordinal);

            if (priv < 0 || classify < 0)
                return "a landmark in Capture was not found";

            if (!(priv < head && head < classify))
                return "the Gauntlet return is not between the private-event check and the classify call";

            return null;
        }

        private static string Hooks() => Norm(PooledLootSourceText.Read(HooksPath));

        [TestMethod]
        public void DamageEntryPoint_CallsGauntletFirst() => Assert.IsNull(EntryFault(Hooks(), DamageSig, DamageWe));

        [TestMethod]
        public void ContactEntryPoint_CallsGauntletFirst() => Assert.IsNull(EntryFault(Hooks(), ContactSig, ContactWe));

        [TestMethod]
        public void EntryPins_FaultOnMutations()
        {
            var src = Hooks();

            // the call removed from one entry point
            var noDamage = src.Replace("            " + GauntletCall + "\n            " + DamageWe, "            " + DamageWe);
            Assert.AreNotEqual(src, noDamage, "mutation did not apply");
            Assert.IsNotNull(EntryFault(noDamage, DamageSig, DamageWe));

            var noContact = src.Replace("            " + GauntletCall + "\n            " + ContactWe, "            " + ContactWe);
            Assert.AreNotEqual(src, noContact, "mutation did not apply");
            Assert.IsNotNull(EntryFault(noContact, ContactSig, ContactWe));

            // the call moved after the World Event call
            var swapped = src.Replace("            " + GauntletCall + "\n            " + DamageWe, "            " + DamageWe + "\n            " + GauntletCall);
            Assert.AreNotEqual(src, swapped, "mutation did not apply");
            Assert.IsNotNull(EntryFault(swapped, DamageSig, DamageWe));

            // a different argument list
            var wrongArgs = src.Replace(GauntletCall, "ACE.Server.Avatars.Gauntlet.GauntletHooks.OnContact(victim, null);");
            Assert.IsNotNull(EntryFault(wrongArgs, DamageSig, DamageWe));
        }

        [TestMethod]
        public void Feed_ReturnsOnAGauntletCopy_AfterPrivateEvent_BeforeClassify() => Assert.IsNull(FeedFault(Norm(PooledLootSourceText.Read(FeedPath))));

        [TestMethod]
        public void FeedPin_FaultsOnMutations()
        {
            var src = Norm(PooledLootSourceText.Read(FeedPath));

            Assert.IsNotNull(FeedFault(src.Replace(FeedHead, "")), "head removed");
            Assert.IsNotNull(FeedFault(src.Replace(FeedHead, FeedHead.Replace("InGauntletCopy(player)", "false"))), "receiver test dropped");
            Assert.IsNotNull(FeedFault(src.Replace(FeedHead, FeedHead.Replace("return;", "break;"))), "return changed");

            // moved after Classify
            var moved = src.Replace("            " + FeedHead, "");
            var at = moved.IndexOf(ClassifyCall, StringComparison.Ordinal);
            var eol = moved.IndexOf('\n', at) + 1;
            moved = moved.Substring(0, eol) + "            " + FeedHead + "\n" + moved.Substring(eol);
            Assert.IsNotNull(FeedFault(moved), "moved after classify");
        }
    }
}
