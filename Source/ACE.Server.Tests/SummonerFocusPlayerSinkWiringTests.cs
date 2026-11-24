using System;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Tests.ThreadDungeons;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Source-scan pin for the Summoner wielder cut at Player.TakeDamage, the player-victim physical sink. A focus
    /// wielder's melee or missile hit on a player (Player.DamageTarget's targetPlayer branch) reaches this overload
    /// through the DamageEvent forwarder and never reaches Creature.TakeDamage, whose HitMod read cuts every other
    /// physical hit. The overload needs a live Session and world to drive, so its text is pinned instead.
    /// </summary>
    [TestClass]
    public class SummonerFocusPlayerSinkWiringTests
    {
        private const string SinkSignature = "public int TakeDamage(WorldObject source, DamageType damageType, float _amount, BodyPart bodyPart, bool crit = false, AttackConditions attackConditions = AttackConditions.None, bool sendDefenderNotification = true, bool isHazard = false, DamageAttackKind? attackKind = null)";

        private const string Cut = "_amount *= SummonerFocus.WielderDamageMod(source, this);";

        private static string Source() => PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Player_Combat.cs");

        private static string Code(string text) => Regex.Replace(text, @"/\*.*?\*/|//[^\n]*", string.Empty, RegexOptions.Singleline);

        private static int CountOf(string haystack, string needle)
        {
            var count = 0;

            for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
                count++;

            return count;
        }

        private static void AssertBefore(string body, string earlier, string later, string because)
        {
            var a = body.IndexOf(earlier, StringComparison.Ordinal);
            var b = body.IndexOf(later, StringComparison.Ordinal);

            Assert.IsTrue(a >= 0, $"missing: {earlier}");
            Assert.IsTrue(b >= 0, $"missing: {later}");
            Assert.IsTrue(a < b, because);
        }

        [TestMethod]
        public void Player_sink_applies_the_wielder_cut_exactly_once()
        {
            var body = Code(PooledLootSourceText.MethodBody(Source(), SinkSignature));

            Assert.AreEqual(1, CountOf(body, Cut));
            Assert.AreEqual(1, CountOf(body, "SummonerFocus."), "the cut is the sink's only focus read");
        }

        [TestMethod]
        public void Player_sink_cuts_after_the_echo_reentry_and_before_the_hit_is_rounded()
        {
            var body = Code(PooledLootSourceText.MethodBody(Source(), SinkSignature));

            AssertBefore(body, "return TakeDamageOutsideHuntersEchoScope(", Cut, "past the Echo re-entry, so a re-entered call is cut once");
            AssertBefore(body, "negative damage, this shouldn't happen", Cut, "past the negative-damage refusal");
            AssertBefore(body, Cut, "var amount = (uint)Math.Round(_amount);", "before the hit is rounded");
            AssertBefore(body, Cut, "UpdateVitalDelta(Health, (int)-amount)", "before the vital write");
        }

        [TestMethod]
        public void Player_DamageTarget_and_the_DamageEvent_forwarder_never_cut_on_their_own()
        {
            var src = Source();
            var damageTarget = Code(PooledLootSourceText.MethodBody(src, "public DamageEvent DamageTarget(Creature target, WorldObject damageSource, float damageMultiplier = 1.0f, bool primaryTarget = true, bool powerBarSwing = true)"));
            var forwarder = Code(PooledLootSourceText.MethodBody(src, "public int TakeDamage(WorldObject source, DamageEvent damageEvent)"));

            Assert.AreEqual(0, CountOf(damageTarget, "SummonerFocus."), "a player target is cut in the sink; a monster target in Creature.TakeDamage");
            StringAssert.Contains(damageTarget, "targetPlayer.TakeDamage(this, damageEvent);");
            Assert.AreEqual(0, CountOf(forwarder, "SummonerFocus."));
        }
    }
}
