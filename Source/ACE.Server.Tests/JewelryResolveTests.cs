using ACE.Server.Managers;
using ACE.Server.Tests.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using static ACE.Server.Tests.JewelryAspectTestKit;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Jewelry Aspect Resolve, now a chance on a kill to remove one monster-cast debuff. The pure rules (the roll, the
    /// eligible-debuff selection, the uniform pick) are covered in JewelryAspectMathTests and the chance getter in
    /// JewelryFortitudeTests. The kill hook needs a live landblock and a Player with a session to drive, so its wiring is
    /// pinned against the source text, along with the absence of the retired duration-shortening Resolve.
    /// </summary>
    [TestClass]
    public class JewelryResolveTests
    {
        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();
        }

        [TestInitialize]
        public void SeedTunables() => Seed();

        [TestCleanup]
        public void RestoreTunables() => Restore();

        [TestMethod]
        public void DefaultsAreTwoPercentPerSlot_CappedAtOneHundredPercent()
        {
            Assert.AreEqual(0.02, DefaultPropertyManager.DefaultDoubleProperties["jewelry_aspects_resolve_per_slot"].Item, 1e-12);
            Assert.AreEqual(1.0, DefaultPropertyManager.DefaultDoubleProperties["jewelry_aspects_resolve_cap"].Item, 1e-12);
        }

        [TestMethod]
        public void SourceScan_DeathHook_RollsForTheKillerOrTheOwnerOfAKillingPet_AfterTheRewardlessReturns()
        {
            var text = Flat(PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Creature_Death.cs"));
            var body = Flat(PooledLootSourceText.MethodBody(text, "public virtual DeathMessage OnDeath(DamageHistoryInfo lastDamager, DamageType damageType, bool criticalHit = false)"));

            var hook = body.IndexOf("if (lastDamager?.TryGetPetOwnerOrAttacker() is Player resolvePlayer) resolvePlayer.ApplyJewelryResolveCreatureDeath(this);", System.StringComparison.Ordinal);
            var classAbilities = body.IndexOf("killingPlayer.ApplyCreatureDeathClassAbilities(this);", System.StringComparison.Ordinal);
            var rewardless = body.IndexOf("IsRewardlessDeath(IsPuzzleAmbush)", System.StringComparison.Ordinal);

            Assert.IsTrue(hook >= 0, "OnDeath must roll Resolve through the pet-owner-aware killer");
            Assert.IsTrue(rewardless >= 0 && hook > rewardless, "a rewardless death grants nothing, Resolve included");
            Assert.IsTrue(classAbilities >= 0 && hook > classAbilities, "the Resolve roll sits after the killing-player hooks, outside their pet-excluding test");
            Assert.AreEqual(1, Count(body, "ApplyJewelryResolveCreatureDeath("), "one roll per death");
        }

        [TestMethod]
        public void SourceScan_PlayerMethod_IsGatedByTheJewelrySwitchOnly_AndSkipsPlayersAndPets()
        {
            var text = PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Player_JewelryAspects.cs");
            var death = Flat(PooledLootSourceText.MethodBody(text, "public void ApplyJewelryResolveCreatureDeath(Creature victim)"));

            StringAssert.Contains(death, "if (victim == null || victim == this || victim is Player || victim is Pet) return;");
            Assert.IsFalse(death.Contains("class_abilities_enabled") || Flat(PooledLootSourceText.MethodBody(text, "public double GetResolveChance()")).Contains("class_abilities_enabled"), "Resolve must run with class abilities off");

            var chance = Flat(PooledLootSourceText.MethodBody(text, "public double GetResolveChance()"));
            StringAssert.Contains(chance, "PropertyManager.GetBool(\"jewelry_aspects_enabled\")");
            StringAssert.Contains(chance, "enabled, false);");
        }

        [TestMethod]
        public void SourceScan_Removal_GoesThroughTheCachingAwareDispel_AndTellsThePlayer()
        {
            var text = PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Player_JewelryAspects.cs");
            var remove = Flat(PooledLootSourceText.MethodBody(text, "private PropertiesEnchantmentRegistry RemoveOneResolvableDebuff("));

            StringAssert.Contains(remove, "EnchantmentManager.Dispel(layers);");
            StringAssert.Contains(remove, "JewelryAspectMath.SelectAllLayers(entry, EnchantmentManager.GetEnchantments(entry.SpellCategory)");
            Assert.AreEqual(1, Count(Flat(PooledLootSourceText.MethodBody(text, "public void ApplyJewelryResolveCreatureDeath(Creature victim)")), "GameMessageSystemChat("), "the spell is named once, however many layers go");
            StringAssert.Contains(remove, "EnchantmentManager.GetEnchantments_TopLayer(EnchantmentTypeFlags.Undef)");
            StringAssert.Contains(Flat(text), "$\"Your Resolve shakes off {spell.Name}.\"");

            var caching = PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Managers/EnchantmentManagerWithCaching.cs");
            StringAssert.Contains(caching, "public override void Dispel(PropertiesEnchantmentRegistry entry)");
        }

        [TestMethod]
        public void SourceScan_TheDurationShorteningResolve_IsGone()
        {
            var enchantments = PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Managers/EnchantmentManager.cs");
            var result = PooledLootSourceText.Read("Source/ACE.Server/Entity/AddEnchantmentResult.cs");
            var dot = PooledLootSourceText.Read("Source/ACE.Server/MonsterEffects/Effects/DotEffect.cs");
            var math = PooledLootSourceText.Read("Source/ACE.Server/Entity/JewelryAspectMath.cs");
            var player = PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Player_JewelryAspects.cs");

            foreach (var token in new[] { "ApplyResolve", "ResolveDuration", "ResolveTicks", "ResolvedTicks", "GetResolveAgainst", "WriteClassAbilityDebuff" })
            {
                foreach (var (name, source) in new[] { ("EnchantmentManager", enchantments), ("AddEnchantmentResult", result), ("DotEffect", dot), ("JewelryAspectMath", math), ("Player_JewelryAspects", player) })
                    Assert.IsFalse(source.Contains(token), $"{name} still mentions {token}");
            }

            StringAssert.Contains(Flat(result), "WorldObject caster, bool equip = false, bool isWeaponSpell = false)");
        }
    }
}