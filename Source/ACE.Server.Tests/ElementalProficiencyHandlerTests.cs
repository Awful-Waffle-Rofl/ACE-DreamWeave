using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Server.ClassAbilities;
using ACE.Server.ClassAbilities.Abilities;
using ACE.Server.Managers;
using ACE.Server.Tests.ThreadDungeons;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The Elemental Proficiency ability handler (ElementalRendAbility, id and quest-key name unchanged): its
    /// definition, its readout, and source-scan pins on the timed-effect application in
    /// Player_ElementalProficiency.cs (Player's static initializer cannot run in this host, so the Player
    /// partial is pinned by text rather than called). Every key the readout reads is seeded to its shipped
    /// default before each test and put back after, so the class passes alone and in any order.
    /// </summary>
    [TestClass]
    public class ElementalProficiencyHandlerTests
    {
        private const double Tol = 1e-9;

        private static readonly string[] DoubleKeys =
        {
            "fire_percent_per_rank", "lightning_percent_per_rank", "frost_slow_per_rank", "acid_fraction_per_rank",
            "pierce_ignore_per_rank", "slashcrit_points_per_rank", "bludgeon_stun_base_seconds",
            "bludgeon_stun_step_seconds", "affinity_cap",
        };

        [TestInitialize]
        public void Seed()
        {
            foreach (var k in DoubleKeys)
            {
                var full = ElementalProficiencyMath.KeyPrefix + k;
                Assert.IsTrue(PropertyManager.ModifyDouble(full, DefaultPropertyManager.DefaultDoubleProperties[full].Item), full);
            }
        }

        [TestCleanup]
        public void Restore()
        {
            foreach (var k in DoubleKeys)
            {
                var full = ElementalProficiencyMath.KeyPrefix + k;
                PropertyManager.ModifyDouble(full, DefaultPropertyManager.DefaultDoubleProperties[full].Item);
            }
        }

        private static string Read(string path) => PooledLootSourceText.Read(path);

        private static List<ClassAbilityReadout> All(ClassAbilityReadout primary)
        {
            var lines = new List<ClassAbilityReadout> { primary };
            if (primary.Secondary != null)
                lines.AddRange(primary.Secondary);
            return lines;
        }

        [TestMethod]
        public void Definition_KeepsTheQuestKeyAndIdAndCarriesTheOwnerFacingText()
        {
            var def = new ElementalRendAbility().Definition;

            Assert.AreEqual(ClassAbilityId.ElementalRend, def.Id);
            Assert.AreEqual("elementalrend", def.Name, "Name is the quest-key suffix that stores ranks");
            Assert.AreEqual("Elemental Proficiency", def.DisplayName);
            Assert.AreEqual(Skill.WarMagic, def.AffinitySkill);
            Assert.AreEqual(3, def.MaxRank);

            Assert.AreEqual(
                "Your damaging spells apply an effect by element when they land on a monster. " +
                "Fire: +10/20/30% damage. " +
                "Lightning: the target takes 10/20/30% more damage from all sources for 10 seconds. " +
                "Frost: the target is slowed by 20/40/60% for 10 seconds. " +
                "Acid: the target burns for 20/40/60% of the hit over 6 seconds; a new acid hit refreshes the burn. " +
                "Bludgeon: the target is stunned for 2/3/4 seconds, then cannot be stunned again for 8 seconds. " +
                "Pierce: ignores 10/20/30% of the target's resistance or deals 10/20/30% more damage, whichever is greater. " +
                "Slash: +5/10/15 percentage points of critical chance. " +
                "Higher War Magic strengthens the Fire, Lightning, Acid and Pierce effects.",
                def.Description);
        }

        [TestMethod]
        public void Readout_AtEachRank_ReportsTheDesignTable()
        {
            for (var rank = 1; rank <= 3; rank++)
            {
                var lines = All(ElementalRendAbility.BuildReadout(rank, 1.0, 0.0));
                Assert.AreEqual(7, lines.Count);

                Assert.AreEqual(10.0 * rank, lines.Single(x => x.Label == "fire dmg").Effective, Tol);
                Assert.AreEqual(10.0 * rank, lines.Single(x => x.Label == "dmg taken").Effective, Tol);
                Assert.AreEqual(20.0 * rank, lines.Single(x => x.Label == "slow").Effective, Tol);
                Assert.AreEqual(20.0 * rank, lines.Single(x => x.Label == "burn of hit").Effective, Tol);
                Assert.AreEqual(10.0 * rank, lines.Single(x => x.Label == "resist ignored").Effective, Tol);
                Assert.AreEqual(5.0 * rank, lines.Single(x => x.Label == "crit chance").Effective, Tol);
                Assert.AreEqual(1.0 + rank, lines.Single(x => x.Label == "stun").Effective, Tol, "2/3/4 seconds");
            }
        }

        [TestMethod]
        public void Readout_RankZero_IsAnEmptyPrimaryWithNoSecondaries()
        {
            var primary = ElementalRendAbility.BuildReadout(0, 1.0, 0.0);

            Assert.AreEqual(0.0, primary.Effective, Tol);
            Assert.IsNull(primary.Secondary);
        }

        [TestMethod]
        public void Readout_AffinityAndGear_ReachOnlyFireLightningAcidAndPierce_AndTheCapBites()
        {
            var cap = PropertyManager.GetDouble(ElementalProficiencyMath.KeyPrefix + "affinity_cap").Item;

            // a saturating multiplier: the cap, not the multiplier, decides the Affinity term
            var lines = All(ElementalRendAbility.BuildReadout(3, 3.0, 0.10));

            var fire = lines.Single(x => x.Label == "fire dmg");
            Assert.AreEqual(30.0 * (1.0 + cap) * 1.10, fire.Effective, 1e-6);
            Assert.AreEqual("affinity cap", fire.CapNote);

            foreach (var line in lines)
                Assert.AreEqual(line.Effective, line.Skill + line.Affinity + line.Gear, 1e-6, line.Label);

            foreach (var label in new[] { "slow", "crit chance", "stun" })
            {
                var line = lines.Single(x => x.Label == label);
                Assert.AreEqual(0.0, line.Affinity, Tol, label);
                Assert.AreEqual(0.0, line.Gear, Tol, label);
                Assert.IsNull(line.CapNote, label);
            }

            // control: Frost stays at its table value however large the multiplier and gear
            Assert.AreEqual(60.0, lines.Single(x => x.Label == "slow").Effective, Tol);
        }

        [TestMethod]
        public void Handler_RollsNothing_AndDelegatesToThePlayerMethod()
        {
            var src = Read("Source/ACE.Server/ClassAbilities/Abilities/ElementalRendAbility.cs");

            Assert.IsFalse(src.Contains("RendChance"), "the proc path is retired");
            Assert.IsFalse(src.Contains("ThreadSafeRandom"), "no roll");
            Assert.IsFalse(src.Contains("elementalrend_chance"), "the retired chance keys are not read");
            Assert.IsFalse(PooledLootSourceText.MethodBody(src, "public void OnSpellHit(").Contains("IsClassAbilitySpawned"), "child projectiles apply the effect too");
            StringAssert.Contains(src, "caster.ApplyElementalProficiencySpellHit(primaryTarget, projectile)");
            StringAssert.Contains(src, "public static SpellId? GetVulnerabilitySpell(DamageType damageType, uint spellLevel)");
        }

        [TestMethod]
        public void PlayerMethod_AppliesTheTimedEffects_WithTheDesignChatLines()
        {
            var body = PooledLootSourceText.MethodBody(
                Read("Source/ACE.Server/WorldObjects/Player_ElementalProficiency.cs"),
                "public void ApplyElementalProficiencySpellHit(");

            StringAssert.Contains(body, "GetElementalProficiencyRank(target)");
            StringAssert.Contains(body, "ElementalProficiencyMath.ShouldApplyTimedEffect(ElementalProficiencyMath.IsEligibleTarget(target), target is Player, rank, type)");
            Assert.IsTrue(body.IndexOf("ShouldApplyTimedEffect(", StringComparison.Ordinal) < body.IndexOf("switch (type)", StringComparison.Ordinal),
                "the gate runs before any effect is applied");
            StringAssert.Contains(body, "TryApplyElementalLightning(");
            StringAssert.Contains(body, "TryApplyClassAbilityFrostSlow(");
            StringAssert.Contains(body, "TryApplyClassAbilityStun(");
            StringAssert.Contains(body, "ElementalRendAbility.GetVulnerabilitySpell(");
            StringAssert.Contains(body, "$\"Elemental Proficiency: Your lightning leaves {target.Name} exposed to all damage!\"");
            StringAssert.Contains(body, "$\"Elemental Proficiency: Your frost slows {target.Name}!\"");
            StringAssert.Contains(body, "$\"Elemental Proficiency: Your bludgeoning spell stuns {target.Name}!\"");

            // Fire, Pierce and Slash are applied in the damage calc, never here
            Assert.IsFalse(body.Contains("DamageType.Fire"));
            Assert.IsFalse(body.Contains("DamageType.Pierce"));
            Assert.IsFalse(body.Contains("DamageType.Slash"));
        }

        [TestMethod]
        public void AcidBurn_HasItsOwnTracker_RefreshesARunningBurn_AndSkipsTheEnchantmentRegistry()
        {
            var full = Read("Source/ACE.Server/WorldObjects/Player_ElementalProficiency.cs");

            // the entry point only computes and ENQUEUES onto the caster's own queue; it never touches the tracker
            var entry = PooledLootSourceText.MethodBody(full, "private void ApplyElementalAcidBurn(");
            StringAssert.Contains(entry, "projectile.LastAppliedDamage");
            StringAssert.Contains(entry, "chain.AddAction(this, () => StartElementalAcidBurn(");
            Assert.IsFalse(entry.Contains("elementalAcidBurns"), "caster-side tracker must only be mutated on the caster's queue");

            StringAssert.Contains(full, "private readonly ElementalAcidBurnTracker elementalAcidBurns = new ElementalAcidBurnTracker();");

            // the tracker owns new-versus-refresh; the chat line fires only for a new burn
            var burn = PooledLootSourceText.MethodBody(full, "private void StartElementalAcidBurn(");
            StringAssert.Contains(burn, "elementalAcidBurns.Apply(target.Guid.Full, tickAmount, ticks, intervalSeconds,");
            StringAssert.Contains(burn, "if (plan == ElementalAcidBurnTracker.Plan.StartNew)");
            StringAssert.Contains(burn, "$\"Elemental Proficiency: Your acid burns {target.Name}!\"");
            StringAssert.Contains(burn, "IncomingDamageOrigin.Secondary");
            StringAssert.Contains(burn, "ScheduleElementalAcidTick);");

            // the public hit method (projectile thread) never mentions the tracker either
            var hit = PooledLootSourceText.MethodBody(full, "public void ApplyElementalProficiencySpellHit(");
            Assert.IsFalse(hit.Contains("elementalAcidBurns"));

            // the scheduler runs every tracker action on THIS player's own queue, after the delay
            var tick = PooledLootSourceText.MethodBody(full, "private void ScheduleElementalAcidTick(");
            StringAssert.Contains(tick, "chain.AddDelaySeconds(delaySeconds);");
            StringAssert.Contains(tick, "chain.AddAction(this, action);");

            // the reset clears the tracker
            StringAssert.Contains(PooledLootSourceText.MethodBody(full, "private void ClearElementalProficiencyTransientState("), "elementalAcidBurns.Clear();");

            // never the Rogue Acid Proc's table, and never the enchantment registry (the Focus DoT bonus)
            Assert.IsFalse(full.Contains("acidProcDots"));
            Assert.IsFalse(full.Contains("CreateEnchantment"));
            Assert.IsFalse(full.Contains("EnchantmentManager"));

            var buffs = Read("Source/ACE.Server/WorldObjects/Player_ClassAbilityBuffs.cs");
            StringAssert.Contains(buffs, "ClearElementalProficiencyTransientState();");
        }

        [TestMethod]
        public void TimedEffectGate_NeedsAnEligibleNonPlayerTarget_ARank_AndATimedElement()
        {
            var timed = new[] { DamageType.Electric, DamageType.Cold, DamageType.Acid, DamageType.Bludgeon };

            foreach (var type in timed)
            {
                Assert.IsTrue(ElementalProficiencyMath.ShouldApplyTimedEffect(true, false, 1, type), type.ToString());
                Assert.IsTrue(ElementalProficiencyMath.ShouldApplyTimedEffect(true, false, 3, type), type.ToString());

                Assert.IsFalse(ElementalProficiencyMath.ShouldApplyTimedEffect(false, false, 3, type), type + ": ineligible target");
                Assert.IsFalse(ElementalProficiencyMath.ShouldApplyTimedEffect(true, true, 3, type), type + ": PvP, even if eligibility ever said yes");
                Assert.IsFalse(ElementalProficiencyMath.ShouldApplyTimedEffect(true, false, 0, type), type + ": no rank");
                Assert.IsFalse(ElementalProficiencyMath.ShouldApplyTimedEffect(true, false, -1, type), type + ": negative rank");
            }

            // the instant elements act inside the damage calculation, and the rest carry no effect at all
            foreach (var type in new[] { DamageType.Fire, DamageType.Pierce, DamageType.Slash, DamageType.Nether, DamageType.Health, DamageType.Base, DamageType.Elemental })
                Assert.IsFalse(ElementalProficiencyMath.ShouldApplyTimedEffect(true, false, 3, type), type.ToString());

            // every timed element is also an element with an effect
            foreach (var type in timed)
                Assert.IsTrue(ElementalProficiencyMath.HasEffect(type), type.ToString());
        }

        [TestMethod]
        public void RetiredChanceKeys_AreGoneFromTheDefaults()
        {
            Assert.IsFalse(DefaultPropertyManager.DefaultDoubleProperties.ContainsKey("class_ability_elementalrend_chance_base"));
            Assert.IsFalse(DefaultPropertyManager.DefaultDoubleProperties.ContainsKey("class_ability_elementalrend_chance_step"));
        }
    }
}
