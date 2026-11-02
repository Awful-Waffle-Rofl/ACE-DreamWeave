using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.ClassAbilities;
using ACE.Server.ClassAbilities.Abilities;
using ACE.Server.Managers;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Blood Mage buff pass, Hemomancy / Crimson Harvest / Heal Boost Rating wiring.
    ///
    /// Most of it is SOURCE SCANS, because the live halves need a Player and a landblock, neither of which
    /// this test project can build: every enumerated heal site calls Player.OnHealthRestored, the two
    /// Hemomancy damage legs this slice owns read GetHemomancyDamageMod, the retired Hemomancy getters are
    /// gone, Crimson Harvest reads its per-rank extras through CrimsonHarvestMath.SecondaryTargetCap, and
    /// Creature.GetDamageRating adds Heal Boost Rating's conversion of its own granted rating.
    ///
    /// The Heal Boost conversion itself is exercised for real. Those tests read the three
    /// class_ability_healboostrating_damage_conversion_r* keys, so they are seeded to their shipped
    /// defaults here and restored in a finally (PropertyManager's caches are process-static).
    /// </summary>
    [TestClass]
    public class BloodMagePackageCWiringTests
    {
        private static readonly string[] ConversionKeys =
        {
            "class_ability_healboostrating_damage_conversion_r1",
            "class_ability_healboostrating_damage_conversion_r2",
            "class_ability_healboostrating_damage_conversion_r3",
        };

        private static readonly double[] ConversionDefaults = { 0.30, 0.60, 1.00 };

        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            // seeds the server-tunable property cache from hardcoded defaults, no database involved
            DefaultPropertyManager.LoadDefaultProperties();
        }

        private static string ServerSource(params string[] relative)
        {
            var path = Path.Combine(new[] { ClassAbilityAffinityDeclarationTests.RepoRoot(), "Source", "ACE.Server" }.Concat(relative).ToArray());

            Assert.IsTrue(File.Exists(path), $"source file not found: {path}");

            return File.ReadAllText(path).Replace("\r\n", "\n");
        }

        /// <summary>The body of the first method whose signature contains <paramref name="signature"/>, brace-matched.</summary>
        private static string MethodBody(string source, string signature)
        {
            var at = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.IsTrue(at >= 0, $"signature not found: {signature}");

            var open = source.IndexOf('{', at);
            var depth = 0;

            for (var i = open; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}' && --depth == 0)
                    return source.Substring(open, i - open + 1);
            }

            Assert.Fail($"unbalanced braces after {signature}");
            return null;
        }

        private static int Count(string haystack, string needle)
            => Regex.Matches(haystack, Regex.Escape(needle)).Count;

        // ---- Heal sites ---------------------------------------------------------------------------------

        [TestMethod]
        public void EveryEnumeratedHealSite_CallsOnHealthRestored_WithTheAppliedDelta()
        {
            var magic = ServerSource("WorldObjects", "WorldObject_Magic.cs");

            // heal spell / gem (non-leech), Drain return (leech only for a Drain), Transfusion share (leech,
            // credited to the recipient player)
            StringAssert.Contains(magic, "hemomancyHealed.OnHealthRestored((uint)boost, false);");
            StringAssert.Contains(magic, "hemomancyDestPlayer.OnHealthRestored(destVitalChange, isDrain);");
            StringAssert.Contains(magic, "hemomancyFellow.OnHealthRestored(fellowGain, true);");

            // heal-over-time tick
            var hot = MethodBody(ServerSource("WorldObjects", "Managers", "EnchantmentManager.cs"), "public void ApplyHealingTick(");
            StringAssert.Contains(hot, "hotHealedPlayer.OnHealthRestored((uint)healAmount, false);");

            // healing kit (credited to the healed target), food/potion, hotspot, Second Wind
            StringAssert.Contains(ServerSource("WorldObjects", "Healer.cs"), "target.OnHealthRestored(actualHealAmount, false);");
            StringAssert.Contains(ServerSource("WorldObjects", "Food.cs"), "player.OnHealthRestored(vitalChange, false);");
            StringAssert.Contains(ServerSource("WorldObjects", "Hotspot.cs"), "player?.OnHealthRestored((uint)iAmount, false);");
            StringAssert.Contains(ServerSource("WorldObjects", "Player_WeaponMods.cs"), "OnHealthRestored((uint)healthRestored, false);");

            // Bloodlust ability AND gear: both route through the one ApplyLifesteal write (leech)
            var lifesteal = MethodBody(ServerSource("ClassAbilities", "Abilities", "BloodlustAbility.cs"), "public static void ApplyLifesteal(");
            StringAssert.Contains(lifesteal, "attacker.OnHealthRestored((uint)applied, true);");
            StringAssert.Contains(ServerSource("WorldObjects", "Player_EquipmentMods.cs"), "BloodlustAbility.ApplyLifesteal(",
                "the Bloodlust gear path must keep routing through ApplyLifesteal, which is where its heal is reported");
        }

        [TestMethod]
        public void HealSites_ReportTheAppliedDelta_NeverTheRequestedAmount()
        {
            // the requested-amount variables at each site must never be what is passed
            var magic = ServerSource("WorldObjects", "WorldObject_Magic.cs");
            Assert.AreEqual(0, Count(magic, "OnHealthRestored((uint)tryBoost"));
            Assert.AreEqual(0, Count(magic, "OnHealthRestored((uint)fellowShare"));

            Assert.AreEqual(0, Count(ServerSource("WorldObjects", "Healer.cs"), "OnHealthRestored(healAmount"));
            Assert.AreEqual(0, Count(ServerSource("WorldObjects", "Food.cs"), "OnHealthRestored((uint)boostValue"));
            Assert.AreEqual(0, Count(ServerSource("ClassAbilities", "Abilities", "BloodlustAbility.cs"), "OnHealthRestored((uint)heal,"));
            Assert.AreEqual(0, Count(ServerSource("WorldObjects", "Player_WeaponMods.cs"), "OnHealthRestored((uint)health,"));
        }

        // ---- Hemomancy damage legs and retirements -------------------------------------------------------

        [TestMethod]
        public void HemomancyWeaponLeg_And_DotLeg_ReadGetHemomancyDamageMod()
        {
            var weapon = MethodBody(ServerSource("ClassAbilities", "Abilities", "HemomancyAbility.cs"), "public void ModifyOutgoingDamage(");
            StringAssert.Contains(weapon, "GetHemomancyDamageMod()");

            var dot = MethodBody(ServerSource("WorldObjects", "Managers", "EnchantmentManager.cs"), "public void ApplyDamageTick(");
            StringAssert.Contains(dot, "tickAmount *= sourcePlayer.GetHemomancyDamageMod();");
            StringAssert.Contains(dot, "if (sourcePlayer != null && targetPlayer == null)",
                "the DoT leg stays PvE only");
        }

        [TestMethod]
        public void HemomancyAbility_HooksTheOutgoingDamageDispatch_AndIsNoLongerAPassiveMarker()
        {
            var handler = ClassAbilityRegistry.GetHandler(ClassAbilityId.Hemomancy);

            Assert.IsInstanceOfType(handler, typeof(IOutgoingDamageAbility));
            Assert.IsFalse(handler is IPassiveStatAbility, "it is dispatched from a hook now");
            Assert.AreEqual(OutgoingDamageDispatchOrder.Modify, ((IOutgoingDamageAbility)handler).DispatchOrder);
            Assert.AreEqual(ACE.Entity.Enum.Skill.Healing, handler.Definition.AffinitySkill);
            CollectionAssert.AreEqual(new[] { 1, 1, 1, 1, 1 }, handler.Definition.CostPerRank);
            Assert.AreEqual(1, handler.Definition.Tier);
        }

        [TestMethod]
        public void RetiredHemomancyGettersAndKeys_AreReadNowhere()
        {
            var root = Path.Combine(ClassAbilityAffinityDeclarationTests.RepoRoot(), "Source", "ACE.Server");

            string[] retired =
            {
                "GetHemomancyDotMod", "GetHemomancyHealFraction", "GetHemomancyDrainMod",
                "class_ability_hemomancy_tick_percent_per_rank", "class_ability_hemomancy_heal_fraction",
                "class_ability_hemomancy_drain_percent_per_rank", "class_ability_crimsonharvest_max_targets",
            };

            var scanned = 0;

            foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)
                    || file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
                    continue;

                scanned++;

                var text = File.ReadAllText(file);

                foreach (var token in retired)
                    Assert.IsFalse(text.Contains(token), $"{Path.GetFileName(file)} still references {token}");
            }

            Assert.IsTrue(scanned > 100, $"positive control: the scan must actually cover the server tree (scanned {scanned})");
        }

        // ---- Crimson Harvest ---------------------------------------------------------------------------

        [TestMethod]
        public void TryCrimsonHarvest_ReadsPerRankExtras_ThroughSecondaryTargetCap()
        {
            var body = MethodBody(ServerSource("WorldObjects", "WorldObject_Magic.cs"), "private void TryCrimsonHarvest(");

            StringAssert.Contains(body, "TryGetClassAbility(ClassAbilityId.CrimsonHarvest, out var harvestRank)");
            StringAssert.Contains(body, "CrimsonHarvestMath.SecondaryTargetCap(harvestRank,");
            StringAssert.Contains(body, "class_ability_crimsonharvest_extra_targets_r1");
            StringAssert.Contains(body, "class_ability_crimsonharvest_extra_targets_r2");
            StringAssert.Contains(body, "class_ability_crimsonharvest_extra_targets_r3");
            Assert.IsFalse(body.Contains("crimsonharvest_max_targets"), "the flat cap is no longer read");
        }

        // ---- Heal Boost Rating -> Damage Rating ---------------------------------------------------------

        private static void WithConversionDefaults(Action body)
        {
            var saved = ConversionKeys.Select(k => PropertyManager.GetDouble(k).Item).ToArray();

            try
            {
                for (var i = 0; i < ConversionKeys.Length; i++)
                    Assert.IsTrue(PropertyManager.ModifyDouble(ConversionKeys[i], ConversionDefaults[i]), $"{ConversionKeys[i]} must be registered");

                body();
            }
            finally
            {
                for (var i = 0; i < ConversionKeys.Length; i++)
                    PropertyManager.ModifyDouble(ConversionKeys[i], saved[i]);
            }
        }

        [TestMethod]
        public void HealBoostConversion_IsOneFourTen_AtTheDefaults()
        {
            WithConversionDefaults(() =>
            {
                // 0.30*3 = 0.9 -> 1, 0.60*6 = 3.6 -> 4, 1.00*10 -> 10
                Assert.AreEqual(1, RatingAbility.HealBoostDamageRatingConversion(3));
                Assert.AreEqual(4, RatingAbility.HealBoostDamageRatingConversion(6));
                Assert.AreEqual(10, RatingAbility.HealBoostDamageRatingConversion(10));

                // a suppressed / unlearned player is granted 0, and 0 converts to 0
                Assert.AreEqual(0, RatingAbility.HealBoostDamageRatingConversion(0));
            });
        }

        [TestMethod]
        public void HealBoostConversion_FollowsTheTunable_AndRoundsHalvesAwayFromZero()
        {
            var saved = PropertyManager.GetDouble(ConversionKeys[0]).Item;

            try
            {
                Assert.IsTrue(PropertyManager.ModifyDouble(ConversionKeys[0], 0.5));
                Assert.AreEqual(2, RatingAbility.HealBoostDamageRatingConversion(3), "1.5 rounds away from zero to 2");
            }
            finally
            {
                PropertyManager.ModifyDouble(ConversionKeys[0], saved);
            }

            Assert.AreEqual(0, RatingAbility.ConvertedRating(3, 0.0));
            Assert.AreEqual(0, RatingAbility.ConvertedRating(0, 1.0));
            Assert.AreEqual(3, RatingAbility.ConvertedRating(5, 0.5), "2.5 -> 3");
        }

        [TestMethod]
        public void OnlyHealBoostRating_CarriesAConversion_AndNoRatingEntryHasAnAffinity()
        {
            var ratingEntries = RatingAbility.GenerateAll().ToList();

            Assert.IsTrue(ratingEntries.Count >= 6, "positive control: the generated family is present");

            foreach (var entry in ratingEntries)
            {
                Assert.IsNull(entry.Definition.AffinitySkill, $"{entry.Definition.Name}: generated rating entries stay unscaled");

                if (entry.Definition.Id == ClassAbilityId.HealBoostRating)
                    Assert.AreEqual("class_ability_healboostrating_damage_conversion_r", entry.DamageConversionTunablePrefix);
                else
                    Assert.IsNull(entry.DamageConversionTunablePrefix, $"{entry.Definition.Name} must not convert");
            }
        }

        [TestMethod]
        public void HealBoostReadout_ReportsTheConversionAsASecondary()
        {
            WithConversionDefaults(() =>
            {
                var hbr = (RatingAbility)ClassAbilityRegistry.GetHandler(ClassAbilityId.HealBoostRating);

                int[] expected = { 0, 1, 4, 10 };

                for (var rank = 0; rank <= 3; rank++)
                {
                    var readout = hbr.GetReadout(null, rank);

                    Assert.AreEqual(RatingAbility.BonusForRank(rank), readout.Effective, 1e-9);
                    Assert.IsNotNull(readout.Secondary);
                    Assert.AreEqual(1, readout.Secondary.Count);
                    Assert.AreEqual(expected[rank], readout.Secondary[0].Effective, 1e-9, $"rank {rank}");
                }

                // the control was Damage Rating until 2026-10-24, when the Berserker pass gave it a crit damage
                // conversion (and so a Secondary of its own); Damage Resist Rating converts nothing
                var damageResist = (RatingAbility)ClassAbilityRegistry.GetHandler(ClassAbilityId.DamageResistRating);
                Assert.IsNull(damageResist.GetReadout(null, 3).Secondary, "control: a non-converting entry has no secondary");
            });
        }

        [TestMethod]
        public void GetDamageRating_AddsTheHealBoostConversion_OfTheOwnGrantedRating()
        {
            var body = MethodBody(ServerSource("WorldObjects", "Creature_Rating.cs"), "public int GetDamageRating()");

            StringAssert.Contains(body,
                "RatingAbility.HealBoostDamageRatingConversion(hbrcPlayer.GetClassAbilityRating(ClassAbilityId.HealBoostRating))");
            // Damage Rating's crit damage conversion (Berserker pass 2026-10-24) joins the sum after this term
            StringAssert.Contains(body, "+ classAbilityBonus + healBoostConversion + critDamageConversion;");
        }
    }
}
