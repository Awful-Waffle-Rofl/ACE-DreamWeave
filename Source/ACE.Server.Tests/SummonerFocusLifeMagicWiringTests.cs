using System;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Tests.ThreadDungeons;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Source-scan pins for the Summoner caster focus multiplier at the three life-magic sinks in WorldObject_Magic.cs:
    /// Harm (HandleCastSpell_Boost), Drain Health (HandleCastSpell_Transfer) and DoT sizing at cast
    /// (CalculateDotEnchantment_StatModValue). Nothing in the project drives these methods live, so the text is the only
    /// thing that can catch a missing, duplicated, ungated or reordered read.
    /// </summary>
    [TestClass]
    public class SummonerFocusLifeMagicWiringTests
    {
        private const string MagicFile = "Source/ACE.Server/WorldObjects/WorldObject_Magic.cs";

        private const string SiteComment = "// Summoner casters (Docs/SummonerCasters/DESIGN.md): the live focus multiplier, read at the hit";

        private static string Body(string signature) =>
            PooledLootSourceText.MethodBody(PooledLootSourceText.Read(MagicFile), signature);

        // The site marker is itself a comment, so its count is read from the raw (comment-bearing) body.
        private static string BodyRaw(string signature) =>
            PooledLootSourceText.MethodBodyBoth(MagicFile, signature).Raw;

        private static string Boost() => Body("private void HandleCastSpell_Boost(");

        private static string Transfer() => Body("private void HandleCastSpell_Transfer(");

        private static string Dot() => Body("public float CalculateDotEnchantment_StatModValue(");

        private static string Flat(string body) => Regex.Replace(body, @"\s+", " ");

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

        // ---- Harm --------------------------------------------------------------------------------------

        [TestMethod]
        public void Harm_reads_the_focus_once_gated_to_health_damage_and_rewrites_both_variables()
        {
            var boost = Boost();
            var flat = Flat(boost);

            Assert.AreEqual(1, CountOf(boost, "SummonerFocus."), "exactly one focus read in Harm");
            Assert.AreEqual(1, CountOf(BodyRaw("private void HandleCastSpell_Boost("), SiteComment));
            Assert.AreEqual(1, CountOf(flat,
                "if (spell.VitalDamageType == DamageType.Health && tryBoost < 0) { var summonerMod = SummonerFocus.HitMod(this, targetCreature); "
                + "if (summonerMod != 1.0f) tryBoost = boost = -(int)Math.Round(-tryBoost * summonerMod); }"),
                "the read is the first statement under the harmful-Health gate, and tryBoost and boost are rewritten together");
            StringAssert.Contains(boost, "tryBoost = boost = -(int)Math.Round(-tryBoost * summonerMod);");
        }

        [TestMethod]
        public void Harm_focus_read_sits_after_the_roll_and_before_every_cap_and_mitigation()
        {
            var boost = Boost();

            AssertBefore(boost, "int boost = tryBoost;", "SummonerFocus.HitMod(", "after the roll and its mods");
            AssertBefore(boost, "SummonerFocus.HitMod(", "PvpChokePoint.N3", "before the PvP normalization");
            AssertBefore(boost, "SummonerFocus.HitMod(", "PvpChokePoint.C3", "before the PvP cap");
            AssertBefore(boost, "SummonerFocus.HitMod(", "PinReductionMath.AppliesTo(", "before the pin rider");
            AssertBefore(boost, "SummonerFocus.HitMod(", "var equippedCloak = targetCreature?.EquippedCloak;", "before the cloak proc");
            AssertBefore(boost, "SummonerFocus.HitMod(", "ApplyPreWriteDamageClassAbilities(", "before the wards");
            AssertBefore(boost, "SummonerFocus.HitMod(", "boost = targetCreature.UpdateVitalDelta(targetCreature.Health, tryBoost);", "before the write");
        }

        // ---- Drain Health ------------------------------------------------------------------------------

        [TestMethod]
        public void Drain_reads_the_focus_once_gated_to_health_loss_and_never_raises_the_gain()
        {
            var transfer = Transfer();
            var flat = Flat(transfer);

            Assert.AreEqual(1, CountOf(transfer, "SummonerFocus."), "exactly one focus read in Drain");
            Assert.AreEqual(1, CountOf(BodyRaw("private void HandleCastSpell_Transfer("), SiteComment));

            // The gain goes through RecomputeDrainGain, which returns min(recomputed, prior): a pet bonus that raises the
            // loss leaves the drainer's heal where it was, and a cut lowers it. Spelled with the named boostMod argument
            // so PvpDamageCapTests' one-line uniqueness pin on the C4 recompute stays exact.
            Assert.AreEqual(1, CountOf(flat,
                "if (isDrain && spell.Source == PropertyAttribute2nd.Health && srcVitalChange > 0) { var summonerMod = SummonerFocus.HitMod(this, transferSource); "
                + "if (summonerMod != 1.0f) { srcVitalChange = (uint)Math.Round(srcVitalChange * summonerMod); "
                + "destVitalChange = PvpRules.RecomputeDrainGain(srcVitalChange, destVitalChange, spell.LossPercent, boostMod: boostMod); } }"),
                "the read is the first statement under the harmful Drain Health gate, and the loss and gain are rewritten together");
        }

        [TestMethod]
        public void Drain_focus_read_sits_after_the_transfer_bounds_and_before_every_cap_and_mitigation()
        {
            var transfer = Transfer();

            AssertBefore(transfer, "destVitalChange = maxDestVitalChange;", "SummonerFocus.HitMod(", "after the TransferCap and overflow bounds");
            AssertBefore(transfer, "SummonerFocus.HitMod(", "PvpChokePoint.N4", "before the PvP normalization");
            AssertBefore(transfer, "SummonerFocus.HitMod(", "PvpChokePoint.C4", "before the PvP cap");
            AssertBefore(transfer, "SummonerFocus.HitMod(", "PinReductionMath.AppliesTo(", "before the pin rider");
            AssertBefore(transfer, "SummonerFocus.HitMod(", "var equippedCloak = targetCreature?.EquippedCloak;", "before the cloak proc");
            AssertBefore(transfer, "SummonerFocus.HitMod(", "MonsterDrainAfterAbsorb(", "before the monster effects");
            AssertBefore(transfer, "SummonerFocus.HitMod(", "ApplyPreWriteDamageClassAbilities(", "before the wards");
            AssertBefore(transfer, "SummonerFocus.HitMod(", "transferSource.UpdateVitalDelta(transferSource.Health", "before the write");
        }

        // ---- DoT sizing at cast ------------------------------------------------------------------------

        [TestMethod]
        public void Dot_reads_the_wielder_cut_once_and_never_the_pet_mods()
        {
            var dot = Dot();

            Assert.AreEqual(1, CountOf(dot, "SummonerFocus.WielderDamageMod(this, creatureTarget)"));
            Assert.AreEqual(1, CountOf(dot, "SummonerFocus."), "the wielder cut is the only focus read");
            Assert.AreEqual(0, CountOf(dot, "SummonerFocus.HitMod("), "pet DoT dealt side is out of scope");
            Assert.AreEqual(1, CountOf(BodyRaw("public float CalculateDotEnchantment_StatModValue("), SiteComment));
            Assert.AreEqual(5, CountOf(dot, "summonerWielderMod"), "one declaration, the three damage returns, and the nether record of the cut (NetherDotWielderCuts)");

            AssertBefore(dot, "var creatureTarget = target as Creature;", "SummonerFocus.WielderDamageMod(", "after the target cast");
            AssertBefore(dot, "SummonerFocus.WielderDamageMod(", "SpellCategory.AetheriaProcHealthOverTimeRaising", "before the first early return");
        }

        [TestMethod]
        public void Dot_cut_multiplies_the_three_damage_returns_and_not_the_heal()
        {
            var dot = Dot();

            StringAssert.Contains(dot, "enchantment_statModVal *= skillMod * elementalDamageMod * damageRatingMod * summonerWielderMod;");
            StringAssert.Contains(dot, "return enchantment_statModVal * damageRatingMod * summonerWielderMod;"); // DF bleed

            var healStart = dot.IndexOf("if (spell.Category == SpellCategory.AetheriaProcHealthOverTimeRaising)", StringComparison.Ordinal);
            var damageStart = dot.IndexOf("if (spell.Category == SpellCategory.AetheriaProcDamageOverTimeRaising)", StringComparison.Ordinal);
            var damageEnd = dot.IndexOf("var player = this as Player;", StringComparison.Ordinal);

            Assert.IsTrue(healStart >= 0 && damageStart > healStart && damageEnd > damageStart, "aetheria branches not found in the expected order");

            var heal = dot.Substring(healStart, damageStart - healStart);
            var aetheriaDamage = dot.Substring(damageStart, damageEnd - damageStart);

            StringAssert.Contains(heal, "return enchantment_statModVal;", "the heal-over-time return is untouched");
            Assert.AreEqual(0, CountOf(heal, "summonerWielderMod"), "a heal is never cut");
            StringAssert.Contains(aetheriaDamage, "return enchantment_statModVal * summonerWielderMod;");
        }
    }
}
