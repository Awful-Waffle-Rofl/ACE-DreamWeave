using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.ClassAbilities;
using ACE.Server.ClassAbilities.Abilities;
using ACE.Server.Managers;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Phase 3 step 2: the live-text formulas ({fn:NAME}) of the migrated class abilities share ONE method with the
    /// mechanic. Two kinds of test per formula. (1) A formula-vs-method test: the text formula at the defaults and under a
    /// retune equals the handler's own pure method called with explicit arguments, including the clamp the old hand-typed
    /// number ignored. (2) A source pin: the handler call site passes ClassAbilityText.Live to that same method, so the
    /// mechanic and the text cannot read the tunable through two different paths.
    ///
    /// Everything reads DictionaryTunableValues, the code defaults or source files, never PropertyManager, so the class
    /// is isolated and passes alone or in any order.
    /// </summary>
    [TestClass]
    public class ClassAbilityTextFormulaPinTests
    {
        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();
        }

        private static ITunableValues Defaults => ClassAbilityText.Defaults;

        private static ITunableValues With(params (string Key, double Value)[] overrides) =>
            new DictionaryTunableValues(overrides.ToDictionary(o => o.Key, o => o.Value));

        private static ClassAbilityDefinition Def(ClassAbilityId id) => ClassAbilityRegistry.Get(id);

        private static double Formula(ClassAbilityId id, string name, ITunableValues values, int rank) =>
            Def(id).TextFormulas[name].Compute(values, rank);

        private static string Source(params string[] relative) =>
            File.ReadAllText(Path.Combine(new[] { ClassAbilityAffinityDeclarationTests.RepoRoot(), "Source", "ACE.Server" }.Concat(relative).ToArray()));

        // ---------------------------------------------------------------------------------------------
        // formula == the handler's own method
        // ---------------------------------------------------------------------------------------------

        [TestMethod]
        public void Overchannel_ArcSurcharge_IsTheManaSurchargeSum_AndFloorsAtZero()
        {
            Assert.AreEqual(2.0, Formula(ClassAbilityId.Overchannel, "arc_surcharge", Defaults, 1), 1e-12);
            Assert.AreEqual(1.0, OverchannelAbility.ManaSurcharge(Defaults, true, false), 1e-12);
            Assert.AreEqual(1.0, OverchannelAbility.ManaSurcharge(Defaults, false, true), 1e-12);
            Assert.AreEqual(0.0, OverchannelAbility.ManaSurcharge(Defaults, false, false), 1e-12);

            var retuned = With(("class_ability_overchannel_mana_surcharge", 0.5), ("class_ability_spellaoe_mana_surcharge", 0.25));
            Assert.AreEqual(0.75, Formula(ClassAbilityId.Overchannel, "arc_surcharge", retuned, 1), 1e-12);

            var negative = With(("class_ability_overchannel_mana_surcharge", -3.0));
            Assert.AreEqual(0.0, Formula(ClassAbilityId.Overchannel, "arc_surcharge", negative, 1), 1e-12, "the mechanic floors the sum at 0");

            StringAssert.Contains(ClassAbilityText.Describe(Def(ClassAbilityId.Overchannel), retuned), "with Spell AOE, an Arc cast costs up to +75%");
            StringAssert.Contains(ClassAbilityText.Describe(Def(ClassAbilityId.Overchannel), retuned), "Boosted casts cost +50% mana");
        }

        [TestMethod]
        public void SanguineReserve_FullPool_IsTheRampAtTheCap_ForEveryRank()
        {
            var perStack = DefaultPropertyManager.DefaultDoubleProperties["class_ability_bloodmage_charge_per_stack"].Item;

            for (var rank = 1; rank <= 3; rank++)
            {
                var cap = BloodChargeMath.StackCap(Defaults, rank);
                var method = BloodChargeMath.DamageMultiplier(cap, BloodChargeMath.PerChargeValue(perStack, 1.0)) - 1.0;

                Assert.AreEqual(method, Formula(ClassAbilityId.SanguineReserve, "full_pool", Defaults, rank), 1e-12, $"rank {rank}");
            }

            // a retuned cap moves the text with the mechanic, and a rank with no cap shows no bonus
            var retuned = With(("class_ability_bloodmage_charge_stack_cap_r1", 10), ("class_ability_bloodmage_charge_per_stack", 0.1));
            Assert.AreEqual((double)BloodChargeMath.DamageMultiplier(10, 0.1) - 1.0, Formula(ClassAbilityId.SanguineReserve, "full_pool", retuned, 1), 1e-9);
            Assert.AreEqual(0.0, BloodChargeMath.FullPoolBonus(Defaults, 0), 1e-12);
        }

        [TestMethod]
        public void Exsanguinate_WorkedExample_IsTheBurstMultiplierAtTheCap()
        {
            var perStack = DefaultPropertyManager.DefaultDoubleProperties["class_ability_bloodmage_charge_per_stack"].Item;
            var burst = DefaultPropertyManager.DefaultDoubleProperties["class_ability_exsanguinate_multiplier"].Item;

            for (var rank = 1; rank <= 3; rank++)
            {
                var cap = BloodChargeMath.StackCap(Defaults, rank);

                Assert.AreEqual(ExsanguinateMath.BurstMultiplier(cap, perStack, burst) - 1.0,
                    Formula(ClassAbilityId.Exsanguinate, "burst_bonus", Defaults, rank), 1e-9, $"rank {rank}");
            }

            // a burst multiplier of 2.5 (the containment lever) shows as +87.5% at a full rank-3 pool
            var retuned = With(("class_ability_exsanguinate_multiplier", 2.5));
            var text = ClassAbilityText.Describe(Def(ClassAbilityId.Exsanguinate), retuned);
            StringAssert.Contains(text, "at 2.5x its normal value: a full pool of 5 gives +87.5% instead of +35%");
        }

        [TestMethod]
        public void Resonance_FullStack_IsTheRampAtTheStackCap()
        {
            var per = new[] { "r1", "r2", "r3" }.Select(r => DefaultPropertyManager.DefaultDoubleProperties["class_ability_resonance_per_stack_" + r].Item).ToArray();

            for (var rank = 1; rank <= 3; rank++)
            {
                var method = ResonanceAbility.DamageMultiplier(rank, 5, per[0], per[1], per[2]) - 1.0;

                Assert.AreEqual(method, Formula(ClassAbilityId.Resonance, "full_stack", Defaults, rank), 1e-9, $"rank {rank}");
            }

            // a retuned cap: 3 stacks of rank 3's 3.6% is +10.8% (the rank tail quotes the full-stack total), and the description quotes the cap
            var retuned = With(("class_ability_resonance_max_stacks", 3));
            Assert.AreEqual("+3.6% per stack (+10.8% when full)", ClassAbilityText.RankTail(Def(ClassAbilityId.Resonance), 3, retuned));
            StringAssert.Contains(ClassAbilityText.Describe(Def(ClassAbilityId.Resonance), retuned), "up to 3.");
        }

        [TestMethod]
        public void Spellsurge_FullStack_IsTheProcChanceBonusAtTheStackCap()
        {
            var per = new[] { "r1", "r2", "r3" }.Select(r => DefaultPropertyManager.DefaultDoubleProperties["class_ability_spellsurge_per_stack_" + r].Item).ToArray();

            for (var rank = 1; rank <= 3; rank++)
            {
                var method = SpellsurgeAbility.ProcChanceBonus(rank, 5, SpellsurgeAbility.PerStack(rank, per[0], per[1], per[2]));

                Assert.AreEqual(method, Formula(ClassAbilityId.Spellsurge, "full_stack", Defaults, rank), 1e-9, $"rank {rank}");
            }

            // the typed "3.3/6.7/10" was rounded from the per-stack rates; the mechanic's product is 3.35/6.65/10
            Assert.AreEqual("+0.67 points per stack (+3.35 when full)", ClassAbilityText.RankTail(Def(ClassAbilityId.Spellsurge), 1, Defaults));
            Assert.AreEqual("+2 points per stack (+10 when full)", ClassAbilityText.RankTail(Def(ClassAbilityId.Spellsurge), 3, Defaults));
            StringAssert.Contains(ClassAbilityText.Describe(Def(ClassAbilityId.Spellsurge), Defaults), "up to 5 stacks");
        }

        [TestMethod]
        public void CrimsonHarvest_InAll_IsTheSecondaryTargetCapPlusThePrimary()
        {
            for (var rank = 1; rank <= 3; rank++)
            {
                var extras = CrimsonHarvestMath.SecondaryTargetCap(rank,
                    DefaultPropertyManager.DefaultLongProperties["class_ability_crimsonharvest_extra_targets_r1"].Item,
                    DefaultPropertyManager.DefaultLongProperties["class_ability_crimsonharvest_extra_targets_r2"].Item,
                    DefaultPropertyManager.DefaultLongProperties["class_ability_crimsonharvest_extra_targets_r3"].Item);

                Assert.AreEqual(extras + 1.0, Formula(ClassAbilityId.CrimsonHarvest, "in_all", Defaults, rank), 0.0, $"rank {rank}");
            }

            var retuned = With(("class_ability_crimsonharvest_extra_targets_r3", 6));
            Assert.AreEqual("up to 7 creatures drained", ClassAbilityText.RankTail(Def(ClassAbilityId.CrimsonHarvest), 3, retuned));
        }

        [TestMethod]
        public void HeavyBlows_FullPower_IsThePowerRatioAtTheFullPowerRetailMod()
        {
            var stretch = DefaultPropertyManager.DefaultDoubleProperties[HeavyBlowsAbility.StretchKey].Item;

            for (var rank = 1; rank <= 3; rank++)
            {
                var method = HeavyBlowsAbility.PowerRatio(HeavyBlowsAbility.FullPowerRetailMod, rank, stretch, 1.0) * HeavyBlowsAbility.FullPowerRetailMod;

                Assert.AreEqual(method, Formula(ClassAbilityId.HeavyBlows, "full_power", Defaults, rank), 1e-12, $"rank {rank}");
            }

            // 150% + stretch x rank: a stretch of 0.5 reads 200 / 250 / 300, and no stretch leaves the retail 150
            var stretched = With((HeavyBlowsAbility.StretchKey, 0.5));
            Assert.AreEqual("full power deals 200%", ClassAbilityText.RankTail(Def(ClassAbilityId.HeavyBlows), 1, stretched));
            Assert.AreEqual("full power deals 250%", ClassAbilityText.RankTail(Def(ClassAbilityId.HeavyBlows), 2, stretched));
            Assert.AreEqual("full power deals 300%", ClassAbilityText.RankTail(Def(ClassAbilityId.HeavyBlows), 3, stretched));

            var unstretched = With((HeavyBlowsAbility.StretchKey, 0.0));
            Assert.AreEqual("full power deals 150%", ClassAbilityText.RankTail(Def(ClassAbilityId.HeavyBlows), 1, unstretched));
            Assert.AreEqual("full power deals 150%", ClassAbilityText.RankTail(Def(ClassAbilityId.HeavyBlows), 3, unstretched));
        }

        [TestMethod]
        public void BerserkRage_DamageBonus_IsTheMultiplierMinusOne_AndZeroWhenTheBonusIsDisabled()
        {
            Assert.AreEqual(0.5, Formula(ClassAbilityId.BerserkRage, "damage_bonus", Defaults, 1), 1e-9);
            Assert.AreEqual(1.5f, BerserkRageAbility.DamageMultiplier(Defaults));

            var retuned = With((BerserkRageAbility.DamageMultiplierKey, 2.0));
            StringAssert.Contains(ClassAbilityText.Describe(Def(ClassAbilityId.BerserkRage), retuned), "you take no damage and deal 100% more.");

            // the mechanic treats a multiplier at or below 1.0 as off, so the text shows 0 rather than a negative bonus
            var off = With((BerserkRageAbility.DamageMultiplierKey, 0.8));
            Assert.AreEqual(1.0f, BerserkRageAbility.DamageMultiplier(off));
            Assert.AreEqual(0.0, Formula(ClassAbilityId.BerserkRage, "damage_bonus", off, 1), 1e-12);
        }

        [TestMethod]
        public void FastAim_LowEnd_IsLowEndBonus_IncludingTheClampToOne()
        {
            var perRank = DefaultPropertyManager.DefaultDoubleProperties["class_ability_fastaim_low_end_per_rank"].Item;

            for (var rank = 1; rank <= 3; rank++)
                Assert.AreEqual(FastAimAbility.LowEndBonus(rank, perRank), (float)Formula(ClassAbilityId.EagleEye, "low_end", Defaults, rank), 1e-9, $"rank {rank}");

            // a per-rank of 0.5 would reach 1.5 at rank 3, but the mechanic clamps the bonus to 1.0 (100 points)
            var retuned = With(("class_ability_fastaim_low_end_per_rank", 0.5));
            Assert.AreEqual("+50 points to the low end", ClassAbilityText.RankTail(Def(ClassAbilityId.EagleEye), 1, retuned));
            Assert.AreEqual("+100 points to the low end", ClassAbilityText.RankTail(Def(ClassAbilityId.EagleEye), 3, retuned));
        }

        [TestMethod]
        public void VolatileWall_HitMultipliers_AreDamageMultiplierOverThePerProjectileBonus()
        {
            for (var rank = 1; rank <= 3; rank++)
            {
                var step = DefaultPropertyManager.DefaultDoubleProperties[VolatileWallAbility.BonusKey(rank)].Item;
                var bonus = VolatileWallAbility.PerProjectileBonus(rank, step, 1.0);

                Assert.AreEqual(VolatileWallAbility.DamageMultiplier(1, bonus), Formula(ClassAbilityId.VolatileWall, "second_hit", Defaults, rank), 1e-9, $"second hit, rank {rank}");
                Assert.AreEqual(VolatileWallAbility.DamageMultiplier(2, bonus), Formula(ClassAbilityId.VolatileWall, "third_hit", Defaults, rank), 1e-9, $"third hit, rank {rank}");
            }

            var retuned = With(("class_ability_volatilewall_bonus_r3", 0.25));
            Assert.AreEqual("+25% per earlier hit (1.0x, 1.25x, 1.5x...)", ClassAbilityText.RankTail(Def(ClassAbilityId.VolatileWall), 3, retuned));
        }

        [TestMethod]
        public void WeakenedBlood_Slow_IsOneMinusTheSpeedFactor_SoTheFloorClampIsExact()
        {
            var floor = DefaultPropertyManager.DefaultDoubleProperties["class_ability_weakenedblood_slow_floor"].Item;

            for (var rank = 1; rank <= 3; rank++)
            {
                var slow = DefaultPropertyManager.DefaultDoubleProperties["class_ability_weakenedblood_slow_r" + rank].Item;

                Assert.AreEqual(1.0 - WeakenedBloodMath.SpeedFactor(slow, floor), Formula(ClassAbilityId.WeakenedBlood, "slow", Defaults, rank), 1e-12, $"rank {rank}");
            }

            // rank 3 retuned to a 90% slow: the floor stops it at 50%, so the text says 50, not the raw 90
            var retuned = With(("class_ability_weakenedblood_slow_r3", 0.9));
            Assert.AreEqual(0.5, Formula(ClassAbilityId.WeakenedBlood, "slow", retuned, 3), 1e-12);
            StringAssert.Contains(ClassAbilityText.RankTail(Def(ClassAbilityId.WeakenedBlood), 1, retuned), ", 10% slow");
            StringAssert.Contains(ClassAbilityText.RankTail(Def(ClassAbilityId.WeakenedBlood), 3, retuned), ", 50% slow");

            // the floor itself drives the "up to N% of the target's speed" clause
            var lowFloor = With(("class_ability_weakenedblood_slow_floor", 0.25));
            StringAssert.Contains(ClassAbilityText.Describe(Def(ClassAbilityId.WeakenedBlood), lowFloor), "The slow can reach 75% of the target's speed");

            // the affinity overload is the same arithmetic the mechanic applies: rank slow x multiplier, then the floor
            Assert.AreEqual(WeakenedBloodMath.SpeedFactor(0.2 * 1.5, floor), WeakenedBloodMath.SpeedFactor(Defaults, 2, 1.5), 1e-12);
        }

        [TestMethod]
        public void RewordedNumberWords_FollowTheirTunables()
        {
            var taunt = Def(ClassAbilityId.Taunt);
            StringAssert.Contains(ClassAbilityText.Describe(taunt, Defaults), "are only 50% as likely to cast spells");
            StringAssert.Contains(ClassAbilityText.Describe(taunt, With(("class_ability_taunt_cast_chance_mult", 0.25))), "are only 25% as likely to cast spells");

            var rallying = Def(ClassAbilityId.RallyingPresence);
            StringAssert.Contains(ClassAbilityText.Describe(rallying, Defaults), "you get 50% of that reduction yourself.");
            StringAssert.Contains(ClassAbilityText.Describe(rallying, With(("class_ability_rallyingpresence_self_share", 0.75))), "you get 75% of that reduction yourself.");
        }

        [TestMethod]
        public void RatingConversionText_FollowsTheConversionTunables()
        {
            var damageRating = Def(ClassAbilityId.DamageRating);
            StringAssert.EndsWith(ClassAbilityText.Describe(damageRating, Defaults), "It also adds part of your total Critical Damage Rating to your Damage Rating.");
            Assert.AreEqual("+6, plus 60% of Critical Damage Rating", ClassAbilityText.RankTail(damageRating, 2, Defaults));
            Assert.AreEqual("+6, plus 45% of Critical Damage Rating", ClassAbilityText.RankTail(damageRating, 2, With(("class_ability_damagerating_critdamage_conversion_r2", 0.45))));

            var thorns = Def(ClassAbilityId.DamageResistRating);
            Assert.AreEqual("+10, Thorns x(1 + 1.5% per DRR)", ClassAbilityText.RankTail(thorns, 3, Defaults));
            Assert.AreEqual("+10, Thorns x(1 + 2% per DRR)", ClassAbilityText.RankTail(thorns, 3, With(("class_ability_damageresistrating_thorns_conversion_r3", 2.0))));

            // Heal Boost's Damage Rating rider is the mechanic's own ConvertedRating at the grant and the fraction
            var heal = Def(ClassAbilityId.HealBoostRating);
            for (var rank = 1; rank <= 3; rank++)
            {
                var fraction = DefaultPropertyManager.DefaultDoubleProperties[RatingAbility.HealBoostDamageConversionPrefix + rank].Item;
                var expected = RatingAbility.ConvertedRating(RatingAbility.BonusForRank(rank), fraction);
                StringAssert.EndsWith(ClassAbilityText.RankTail(heal, rank, Defaults), $"(+{expected} Damage Rating)");
            }

            StringAssert.EndsWith(ClassAbilityText.RankTail(heal, 3, With((RatingAbility.HealBoostDamageConversionPrefix + "3", 0.5))), "(+5 Damage Rating)");
        }

        [TestMethod]
        public void RatingGrant_IsTheRatingBonusTable_ThroughAConstantFormula()
        {
            foreach (var id in new[] { ClassAbilityId.CritRating, ClassAbilityId.CritDamageRating, ClassAbilityId.DamageRating, ClassAbilityId.DamageResistRating, ClassAbilityId.CritResistRating, ClassAbilityId.HealBoostRating })
            {
                var def = Def(id);
                var grant = def.TextFormulas["grant"];

                Assert.IsTrue(grant.IsConstant, def.Name);
                Assert.AreEqual(0, grant.Keys.Length, def.Name + ": a constant formula reads no tunable");

                for (var rank = 1; rank <= 3; rank++)
                {
                    Assert.AreEqual(RatingAbility.BonusForRank(rank), grant.Compute(Defaults, rank), 0.0, $"{def.Name} rank {rank}");
                    StringAssert.StartsWith(ClassAbilityText.RankTail(def, rank, Defaults), $"+{RatingAbility.BonusForRank(rank)}");
                }
            }
        }

        [TestMethod]
        public void CritResistRatingTail_RendersTheGrantAndTheDamageResistConversion_FromTheTunable()
        {
            var crit = Def(ClassAbilityId.CritResistRating);
            Assert.AreEqual("+3, +30% of it as DRR", ClassAbilityText.RankTail(crit, 1, Defaults));
            Assert.AreEqual("+6, +60% of it as DRR", ClassAbilityText.RankTail(crit, 2, Defaults));
            Assert.AreEqual("+10, +100% of it as DRR", ClassAbilityText.RankTail(crit, 3, Defaults));
            Assert.AreEqual("+6, +45% of it as DRR", ClassAbilityText.RankTail(crit, 2, With(("class_ability_critresistrating_damage_conversion_r2", 0.45))));
            StringAssert.EndsWith(ClassAbilityText.Describe(crit, Defaults), "It also adds part of your Critical Resist Rating to your Damage Resist Rating.");
        }

        [TestMethod]
        public void HealBoostTail_RejectsATypedNumber_ButTheFormulaPlaceholderPasses()
        {
            var real = Def(ClassAbilityId.HealBoostRating);
            Assert.AreEqual(0, ClassAbilityText.BareNumbers(real).Count, "the real definition types no number");

            var typed = new ClassAbilityDefinition
            {
                Name = "probe",
                MaxRank = 3,
                DescriptionTemplate = real.DescriptionTemplate,
                RankTailTemplate = new[] { "+{fn:grant|int} (+10 Damage Rating)", "+{fn:grant|int}", "+{fn:grant|int}" },
                TextFormulas = real.TextFormulas,
                AllowedLiterals = real.AllowedLiterals,
            };
            CollectionAssert.AreEqual(new[] { "10" }, ClassAbilityText.BareNumbers(typed).ToArray(), "a typed number in the Heal Boost tail must be flagged");
        }

        [TestMethod]
        public void VolatileElements_UsesTheComputedArticle()
        {
            // Parry's description used to carry the {an} article; the only template that still computes one is Volatile Elements' rank tail.
            var elements = Def(ClassAbilityId.VolatileElements);
            Assert.AreEqual("an 8% chance of the weakest element", ClassAbilityText.RankTail(elements, 1, Defaults));
            Assert.AreEqual("a 16% chance of the weakest element", ClassAbilityText.RankTail(elements, 2, Defaults));
            Assert.AreEqual("a 10% chance of the weakest element", ClassAbilityText.RankTail(elements, 1, With(("class_ability_volatileelements_chance_per_rank", 0.10))));
        }

        [TestMethod]
        public void PerRankTails_ScaleTheTunableByTheRank()
        {
            // The descriptions no longer quote a "(+30% at rank 3)" total; the rank tail renders key x rank.
            Assert.AreEqual("+30% spell damage", ClassAbilityText.RankTail(Def(ClassAbilityId.Overchannel), 3, With(("class_ability_overchannel_percent_per_rank", 0.1))));
            Assert.AreEqual("+30% void damage", ClassAbilityText.RankTail(Def(ClassAbilityId.VoidDamage), 3, With(("class_ability_voiddamage_percent_per_rank", 0.1))));
            Assert.AreEqual("+30% tick damage", ClassAbilityText.RankTail(Def(ClassAbilityId.Withering), 3, With(("class_ability_withering_percent_per_rank", 0.1))));
            Assert.AreEqual("+30% attack speed", ClassAbilityText.RankTail(Def(ClassAbilityId.AttackSpeed), 3, With(("class_ability_attackspeed_percent_per_rank", 0.1))));
        }

        [TestMethod]
        public void ThenListsMinutesAndPerRankTails_RenderMastersWordingAndFollowRetunes()
        {
            var cloaked = Def(ClassAbilityId.CloakedInPower);
            Assert.AreEqual("1.0 seconds of critical Thorns", ClassAbilityText.RankTail(cloaked, 2, Defaults));
            Assert.AreEqual("2.0 seconds of critical Thorns", ClassAbilityText.RankTail(cloaked, 3, With(("class_ability_cloakedinpower_crit_window_r3", 2.0))));

            var battle = Def(ClassAbilityId.BattleHardened);
            Assert.AreEqual("0.05% less damage per Strength point", ClassAbilityText.RankTail(battle, 2, Defaults));

            var soulJump = Def(ClassAbilityId.SoulJump);
            StringAssert.EndsWith(ClassAbilityText.RankTail(soulJump, 2, Defaults), ", once every 4 minutes");
            StringAssert.EndsWith(ClassAbilityText.RankTail(soulJump, 2, With(("class_ability_souljump_cooldown_seconds_step", -60.0))), ", once every 5 minutes");

            var berserk = Def(ClassAbilityId.BerserkRage);
            Assert.AreEqual("once every 90 seconds", ClassAbilityText.RankTail(berserk, 2, Defaults));
            Assert.AreEqual("once every 45 seconds", ClassAbilityText.RankTail(berserk, 3, Defaults));
            Assert.AreEqual("once every 100 seconds", ClassAbilityText.RankTail(berserk, 2, With(("class_ability_berserkrage_cooldown_seconds_r2", 100.0))));

            var acid = Def(ClassAbilityId.AcidProc);
            Assert.AreEqual("+25% poison damage", ClassAbilityText.RankTail(acid, 1, Defaults));
            Assert.AreEqual("+75% poison damage", ClassAbilityText.RankTail(acid, 3, Defaults));
            Assert.AreEqual("+40% poison damage", ClassAbilityText.RankTail(acid, 2, With(("class_ability_acidproc_damage_step", 0.30), ("class_ability_acidproc_damage_base", 0.10))));

            var whirlwind = Def(ClassAbilityId.Whirlwind);
            Assert.AreEqual("1 extra target, +35% stamina", ClassAbilityText.RankTail(whirlwind, 1, Defaults));
            Assert.AreEqual("3 extra targets, +100% stamina", ClassAbilityText.RankTail(whirlwind, 3, Defaults));

            var elements = Def(ClassAbilityId.VolatileElements);
            Assert.AreEqual("an 8% chance of the weakest element", ClassAbilityText.RankTail(elements, 1, Defaults));
            Assert.AreEqual("a 16% chance of the weakest element", ClassAbilityText.RankTail(elements, 2, Defaults));
            Assert.AreEqual("an 11% chance of the weakest element", ClassAbilityText.RankTail(elements, 1, With(("class_ability_volatileelements_chance_per_rank", 0.11))));
        }

        [TestMethod]
        public void SoulJump_Cooldown_IsTheMechanicsClampedSeconds_EvenOnARunawayStep()
        {
            // defaults: 360 / 240 / 120 seconds -> 6/4/2 minutes
            Assert.AreEqual(360.0, Formula(ClassAbilityId.SoulJump, "cooldown", Defaults, 1), 1e-12);
            Assert.AreEqual(120.0, Formula(ClassAbilityId.SoulJump, "cooldown", Defaults, 3), 1e-12);
            Assert.AreEqual(SoulJumpAbility.CooldownSeconds(Defaults, 2), Formula(ClassAbilityId.SoulJump, "cooldown", Defaults, 2), 1e-12);

            // a runaway step would take rank 3 to 360 - 400 = -40 seconds; the mechanic floors it at MinimumCooldownSeconds
            var runaway = With(("class_ability_souljump_cooldown_seconds_base", 360.0), ("class_ability_souljump_cooldown_seconds_step", -200.0));
            Assert.AreEqual(160.0, SoulJumpAbility.CooldownSeconds(runaway, 2), 1e-12);
            Assert.AreEqual(SoulJumpAbility.MinimumCooldownSeconds, SoulJumpAbility.CooldownSeconds(runaway, 3), 1e-12);
            Assert.AreEqual(SoulJumpAbility.MinimumCooldownSeconds, Formula(ClassAbilityId.SoulJump, "cooldown", runaway, 3), 1e-12, "the text quotes the clamped cooldown, not -40 seconds");

            var rendered = ClassAbilityText.Describe(Def(ClassAbilityId.SoulJump), runaway);
            Assert.IsFalse(rendered.Contains("-"), "no negative minutes in: " + rendered);
        }

        [TestMethod]
        public void Taunt_CastChance_IsTheMechanicsClamp()
        {
            Assert.AreEqual(0.5, Formula(ClassAbilityId.Taunt, "cast_chance", Defaults, 1), 1e-12);
            Assert.AreEqual(1.0, TauntAbility.CastChanceMultiplier(2.0), 1e-12);
            Assert.AreEqual(0.0, TauntAbility.CastChanceMultiplier(-1.0), 1e-12);

            var high = With(("class_ability_taunt_cast_chance_mult", 2.0));
            Assert.AreEqual(1.0, Formula(ClassAbilityId.Taunt, "cast_chance", high, 1), 1e-12);
            StringAssert.Contains(ClassAbilityText.Describe(Def(ClassAbilityId.Taunt), high), "are only 100% as likely to cast spells");

            var negative = With(("class_ability_taunt_cast_chance_mult", -0.5));
            StringAssert.Contains(ClassAbilityText.Describe(Def(ClassAbilityId.Taunt), negative), "are only 0% as likely to cast spells");

        }

        [TestMethod]
        public void RallyingPresence_SelfShare_IsTheMechanicsFloor()
        {
            Assert.AreEqual(0.5, Formula(ClassAbilityId.RallyingPresence, "self_share", Defaults, 1), 1e-12);
            Assert.AreEqual(0.5, RallyingPresenceMath.SelfShareFraction(1.0, RallyingPresenceMath.SelfShare(Defaults)), 1e-12);

            var negative = With(("class_ability_rallyingpresence_self_share", -0.5));
            Assert.AreEqual(0.0, Formula(ClassAbilityId.RallyingPresence, "self_share", negative, 1), 1e-12, "the handler floors the share at 0");
            StringAssert.Contains(ClassAbilityText.Describe(Def(ClassAbilityId.RallyingPresence), negative), "you get 0% of that reduction yourself.");
        }

        [TestMethod]
        public void Exsanguinate_BurstMultiplier_IsTheMechanicsFloor()
        {
            Assert.AreEqual(3.0, Formula(ClassAbilityId.Exsanguinate, "burst_multiplier", Defaults, 1), 1e-12);
            Assert.AreEqual(1.0, ExsanguinateMath.EffectiveBurstMultiplier(0.25), 1e-12);
            Assert.AreEqual(1.5, ExsanguinateMath.EffectiveBurstMultiplier(0.25, 1.25), 1e-12, "the gear term is added before the floor");

            var low = With(("class_ability_exsanguinate_multiplier", 0.25));
            Assert.AreEqual(1.0, Formula(ClassAbilityId.Exsanguinate, "burst_multiplier", low, 1), 1e-12);
            StringAssert.Contains(ClassAbilityText.Describe(Def(ClassAbilityId.Exsanguinate), low), "spends the whole pool at 1x its normal value");

            // BurstMultiplier applies the same floor: a 0.25 tunable bursts exactly like 1.0
            Assert.AreEqual(ExsanguinateMath.BurstMultiplier(5, 0.07, 1.0), ExsanguinateMath.BurstMultiplier(5, 0.07, 0.25), 1e-12);
        }
        // ---------------------------------------------------------------------------------------------
        // source pins: the mechanic reads the same method with ClassAbilityText.Live
        // ---------------------------------------------------------------------------------------------

        // Each pin is the FULL call statement, whitespace-normalised: the affinity, gear and rank arguments are part of the
        // pinned text, so a call that drops or swaps one (an affinity set to 1.0, a rank set to 1) no longer matches. A pin
        // that only matched a prefix let five such mutations through review. The last column is how many times the call
        // must appear.
        [DataTestMethod]
        [DataRow("WorldObjects/Player_ClassAbilityBuffs.cs", "return (float)(1.0 + OverchannelAbility.ManaSurcharge(ClassAbilityText.Live, overchannelApplies, spellAoeApplies));", 1)]
        [DataRow("WorldObjects/Player_ClassAbilityBuffs.cs", "BloodChargeMath.StackCap(ClassAbilityText.Live, rank);", 1)]
        [DataRow("WorldObjects/Player_ClassAbilityBuffs.cs", "BloodChargeMath.PerChargeValue(ClassAbilityText.Live, GetClassAbilityAffinityMultiplier(Skill.CreatureEnchantment));", 1)]
        [DataRow("WorldObjects/Player_ClassAbilityBuffs.cs", "ExsanguinateMath.BurstMultiplier(stacks, perStack, ExsanguinateMath.BurstMultiplierTunable(ClassAbilityText.Live), GetEquippedModValue(EquipmentModId.Sanguinate), gearPerStack);", 1)]
        [DataRow("WorldObjects/Player_ClassAbilityBuffs.cs", "ExsanguinateMath.ChargesKept(ClassAbilityText.Live, exsanguinateRank, cap);", 1)]
        [DataRow("WorldObjects/Player_ClassAbilityBuffs.cs", "ResonanceAbility.MaxStacks(ClassAbilityText.Live);", 1)]
        [DataRow("WorldObjects/Player_ClassAbilityBuffs.cs", "ResonanceAbility.PerStack(ClassAbilityText.Live, rank);", 1)]
        [DataRow("WorldObjects/Player_ClassAbilityBuffs.cs", "ResonanceAbility.DamageMultiplierFor(ClassAbilityText.Live, rank, stacks, GetEquippedModValue(EquipmentModId.Harmonics));", 1)]
        [DataRow("WorldObjects/Player_ClassAbilityBuffs.cs", "SpellsurgeAbility.MaxStacks(ClassAbilityText.Live);", 1)]
        [DataRow("WorldObjects/Player_ClassAbilityBuffs.cs", "SpellsurgeAbility.ProcChanceBonusFor(ClassAbilityText.Live, rank, stacks, GetEquippedModValue(EquipmentModId.Surge));", 1)]
        [DataRow("WorldObjects/Player_ClassAbilityBuffs.cs", "var affinity = GetClassAbilityAffinityMultiplier(Skill.Run); return FastAimAbility.LowEndBonus(ClassAbilityText.Live, rank, affinity, gearMod);", 1)]
        [DataRow("WorldObjects/Player_WeakenedBlood.cs", "return WeakenedBloodMath.SpeedFactor(ClassAbilityText.Live, rank, GetClassAbilityAffinityMultiplier(Skill.CreatureEnchantment));", 1)]
        [DataRow("WorldObjects/Player_BerserkRage.cs", "return BerserkRageAbility.DamageMultiplier(ClassAbilityText.Live);", 1)]
        [DataRow("WorldObjects/Player_ArchmageResistance.cs", "var bonus = VolatileWallAbility.PerProjectileBonus(ClassAbilityText.Live, rank, GetClassAbilityAffinityMultiplier(Skill.WarMagic));", 1)]
        [DataRow("WorldObjects/WorldObject_Magic.cs", "var maxTargets = CrimsonHarvestMath.SecondaryTargetCap(ClassAbilityText.Live, harvestRank);", 1)]
        [DataRow("WorldObjects/Monster_Combat.cs", "var m = TauntAbility.CastChanceMultiplier(multReader());", 1)]
        [DataRow("ClassAbilities/Abilities/CrimsonHarvestAbility.cs", "var extras = CrimsonHarvestMath.SecondaryTargetCap(ClassAbilityText.Live, rank);", 1)]
        [DataRow("ClassAbilities/Abilities/ExsanguinateAbility.cs", "var stackCap = BloodChargeMath.StackCap(ClassAbilityText.Live, reserveRank);", 1)]
        [DataRow("ClassAbilities/Abilities/ExsanguinateAbility.cs", "var burstMultiplierTunable = ExsanguinateMath.BurstMultiplierTunable(ClassAbilityText.Live);", 1)]
        [DataRow("ClassAbilities/Abilities/ExsanguinateAbility.cs", "var kept = ExsanguinateMath.ChargesKept(ClassAbilityText.Live, rank, stackCap);", 1)]
        [DataRow("ClassAbilities/Abilities/HeavyBlowsAbility.cs", "var ratio = PowerRatio(ClassAbilityText.Live, damageEvent.PowerMod, rank, attacker.GetClassAbilityAffinityMultiplier(Skill.WeaponTinkering));", 1)]
        [DataRow("ClassAbilities/Abilities/HeavyBlowsAbility.cs", "var skill = (PowerRatio(ClassAbilityText.Live, FullPowerRetailMod, rank, 1.0) - 1.0) * 100.0;", 1)]
        [DataRow("ClassAbilities/Abilities/HeavyBlowsAbility.cs", "var withAffinity = (PowerRatio(ClassAbilityText.Live, FullPowerRetailMod, rank, multiplier) - 1.0) * 100.0;", 1)]
        [DataRow("ClassAbilities/Abilities/SoulJumpAbility.cs", "var cooldownSeconds = CooldownSeconds(ClassAbilityText.Live, rank);", 2)]
        [DataRow("ClassAbilities/Abilities/RallyingPresenceAbility.cs", "var selfShare = RallyingPresenceMath.SelfShare(ClassAbilityText.Live);", 1)]
        [DataRow("ClassAbilities/Abilities/WeakenedBloodAbility.cs", "var rankSlow = WeakenedBloodMath.RankSlow(ClassAbilityText.Live, rank);", 1)]
        [DataRow("ClassAbilities/Abilities/WeakenedBloodAbility.cs", "var factor = WeakenedBloodMath.SpeedFactor(ClassAbilityText.Live, rank, multiplier);", 1)]
        [DataRow("ClassAbilities/Abilities/SpellsurgeAbility.cs", "var perStack = PerStack(ClassAbilityText.Live, rank);", 1)]
        [DataRow("ClassAbilities/Abilities/SanguineReserveAbility.cs", "var perStack = BloodChargeMath.PerStack(ClassAbilityText.Live);", 1)]
        [DataRow("ClassAbilities/Abilities/FastAimAbility.cs", "var perRank = LowEndPerRank(ClassAbilityText.Live);", 1)]
        [DataRow("ClassAbilities/Abilities/VolatileWallAbility.cs", "var rankBonus = RankBonus(ClassAbilityText.Live, rank);", 1)]
        public void HandlerCallSite_PassesClassAbilityTextLive_ToTheMethodTheTextFormulaCalls(string file, string call, int count)
        {
            var text = System.Text.RegularExpressions.Regex.Replace(Source(file.Split('/')), @"\s+", " ");
            var wanted = System.Text.RegularExpressions.Regex.Replace(call, @"\s+", " ");

            var found = 0;
            for (var at = text.IndexOf(wanted, StringComparison.Ordinal); at >= 0; at = text.IndexOf(wanted, at + wanted.Length, StringComparison.Ordinal))
                found++;

            Assert.AreEqual(count, found, $"{file} must contain {wanted} exactly {count} time(s)");
        }

        // the text after "PropertyManager.GetDouble(" / "GetLong(" that the site used to read: a quoted key prefix, or the key expression
        [DataTestMethod]
        [DataRow("WorldObjects/Player_ClassAbilityBuffs.cs", "\"class_ability_overchannel_mana_surcharge\"")]
        [DataRow("WorldObjects/Player_ClassAbilityBuffs.cs", "\"class_ability_spellaoe_mana_surcharge\"")]
        [DataRow("WorldObjects/Player_ClassAbilityBuffs.cs", "\"class_ability_bloodmage_charge_per_stack\"")]
        [DataRow("WorldObjects/Player_ClassAbilityBuffs.cs", "\"class_ability_exsanguinate_multiplier\"")]
        [DataRow("WorldObjects/Player_ClassAbilityBuffs.cs", "\"class_ability_resonance_max_stacks\"")]
        [DataRow("WorldObjects/Player_ClassAbilityBuffs.cs", "\"class_ability_resonance_per_stack_r")]
        [DataRow("WorldObjects/Player_ClassAbilityBuffs.cs", "\"class_ability_spellsurge_max_stacks\"")]
        [DataRow("WorldObjects/Player_ClassAbilityBuffs.cs", "\"class_ability_spellsurge_per_stack_r")]
        [DataRow("WorldObjects/Player_ClassAbilityBuffs.cs", "\"class_ability_fastaim_low_end_per_rank\"")]
        [DataRow("WorldObjects/Player_WeakenedBlood.cs", "\"class_ability_weakenedblood_slow_r")]
        [DataRow("WorldObjects/Player_WeakenedBlood.cs", "\"class_ability_weakenedblood_slow_floor\"")]
        [DataRow("WorldObjects/Player_BerserkRage.cs", "BerserkRageAbility.DamageMultiplierKey")]
        [DataRow("WorldObjects/Player_ArchmageResistance.cs", "VolatileWallAbility.BonusKey")]
        [DataRow("WorldObjects/WorldObject_Magic.cs", "\"class_ability_crimsonharvest_extra_targets_r")]
        [DataRow("ClassAbilities/Abilities/CrimsonHarvestAbility.cs", "\"class_ability_crimsonharvest_extra_targets_r")]
        [DataRow("ClassAbilities/Abilities/HeavyBlowsAbility.cs", "StretchKey")]
        [DataRow("ClassAbilities/Abilities/ExsanguinateAbility.cs", "\"class_ability_exsanguinate_multiplier\"")]
        [DataRow("ClassAbilities/Abilities/RallyingPresenceAbility.cs", "\"class_ability_rallyingpresence_self_share\"")]
        [DataRow("ClassAbilities/Abilities/WeakenedBloodAbility.cs", "\"class_ability_weakenedblood_slow_r\"")]
        [DataRow("ClassAbilities/Abilities/WeakenedBloodAbility.cs", "\"class_ability_weakenedblood_slow_floor\"")]
        [DataRow("ClassAbilities/Abilities/SpellsurgeAbility.cs", "\"class_ability_spellsurge_per_stack_r\"")]
        [DataRow("ClassAbilities/Abilities/SanguineReserveAbility.cs", "\"class_ability_bloodmage_charge_per_stack\"")]
        [DataRow("ClassAbilities/Abilities/FastAimAbility.cs", "\"class_ability_fastaim_low_end_per_rank\"")]
        [DataRow("ClassAbilities/Abilities/VolatileWallAbility.cs", "BonusKey")]
        public void HandlerSite_NoLongerReadsTheTunableInline(string file, string key)
        {
            var text = Source(file.Split('/'));

            foreach (var read in new[] { "PropertyManager.GetDouble(" + key, "PropertyManager.GetLong(" + key })
                Assert.IsFalse(text.Contains(read, StringComparison.Ordinal), $"{file} still has {read}, so the mechanic and the text formula can diverge");
        }

        [TestMethod]
        public void EveryMigratedFormulaAbility_DeclaresAndUsesItsFormulas()
        {
            foreach (var id in new[]
            {
                ClassAbilityId.Overchannel, ClassAbilityId.SanguineReserve, ClassAbilityId.Exsanguinate, ClassAbilityId.Resonance,
                ClassAbilityId.Spellsurge, ClassAbilityId.CrimsonHarvest, ClassAbilityId.HeavyBlows, ClassAbilityId.BerserkRage,
                ClassAbilityId.EagleEye, ClassAbilityId.VolatileWall, ClassAbilityId.WeakenedBlood,
            })
            {
                var def = Def(id);
                Assert.IsTrue(def.TextFormulas != null && def.TextFormulas.Count > 0, $"{def.Name}: expected text formulas");
                Assert.IsTrue(ClassAbilityText.FormulaNames(def).Count == def.TextFormulas.Count, $"{def.Name}: every formula is used");
            }
        }
    }
}
