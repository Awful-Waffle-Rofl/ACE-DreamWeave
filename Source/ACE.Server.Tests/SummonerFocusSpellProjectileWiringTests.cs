using System;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Tests.ThreadDungeons;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Source-scan pin for the Summoner caster focus multiplier at SpellProjectile.DamageTarget (the spell sink).
    /// Nothing in the project drives DamageTarget live, so the text is the only thing that can catch a missing,
    /// duplicated or reordered read.
    /// </summary>
    [TestClass]
    public class SummonerFocusSpellProjectileWiringTests
    {
        private const string Signature = "public void DamageTarget(Creature target, float damage, bool critical, bool critDefended, bool overpower)";

        private static string Body() =>
            PooledLootSourceText.MethodBody(PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/SpellProjectile.cs"), Signature);

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
        public void Focus_multiplier_is_read_once_per_projectile()
        {
            var body = Body();

            Assert.AreEqual(1, CountOf(body, "SummonerFocus.HitMod("), "exactly one read per projectile");
            Assert.AreEqual(1, CountOf(body, "damage *= SummonerFocus.HitMod(ProjectileSource, target, ProjectileLauncher);"));
            Assert.AreEqual(1, CountOf(body, "// Summoner casters (Docs/SummonerCasters/DESIGN.md): the live focus multiplier, read at the hit"));
        }

        /// <summary>
        /// The bolt's launcher (snapshotted at the cast) reaches the wielder cut, so swapping the focus off mid-flight does
        /// not escape it; and the cut the hit took is recorded, with the same arguments, for the Acid burn's sizing.
        /// </summary>
        [TestMethod]
        public void Launcher_reaches_the_wielder_cut_and_the_cut_is_recorded_beside_it()
        {
            var body = Body();
            const string record = "LastAppliedWielderDamageMod = SummonerFocus.WielderDamageMod(ProjectileSource, target, ProjectileLauncher);";

            Assert.AreEqual(1, CountOf(body, record));
            Assert.AreEqual(1, CountOf(body, "LastAppliedWielderDamageMod ="), "recorded once per projectile");
            AssertBefore(body, record, "damage *= SummonerFocus.HitMod(ProjectileSource, target, ProjectileLauncher);", "recorded beside the cut it describes");
            AssertBefore(body, record, "LastAppliedDamage = amount;", "recorded before the post-hit dispatch reads it");
        }

        [TestMethod]
        public void Acid_burn_sizing_reads_the_recorded_cut()
        {
            var entry = PooledLootSourceText.MethodBody(PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Player_ElementalProficiency.cs"), "private void ApplyElementalAcidBurn(");

            Assert.AreEqual(1, CountOf(entry, "ElementalAcidTickAmount(projectile.LastAppliedDamage, projectile.LastAppliedWielderDamageMod, fraction, ticks)"));
        }

        [TestMethod]
        public void Focus_multiplier_sits_after_the_ratings_and_before_every_cap_and_mitigation()
        {
            var body = Body();

            AssertBefore(body, "damage *= damageRatingMod * damageResistRatingMod;", "SummonerFocus.HitMod(", "after the ratings");
            AssertBefore(body, "SummonerFocus.HitMod(", "PvpRules.ApplyDamageCap(PvpChokePoint.C2", "before the PvP cap");
            AssertBefore(body, "SummonerFocus.HitMod(", "ApplyPreWriteDamageClassAbilities(", "before the wards");
            AssertBefore(body, "SummonerFocus.HitMod(", "target.UpdateVitalDelta(target.Health", "before the vital write");
        }

        [TestMethod]
        public void Focus_multiplier_is_health_branch_only()
        {
            var body = Body();

            AssertBefore(body, "Spell.Category == SpellCategory.ManaLowering", "SummonerFocus.HitMod(", "health branch only, never a stamina/mana drain");
        }
    }
}
