using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// The family ability budget, selection and attach (DAMAGE-CEILING-PLAN.md step 6; <see cref="FamilyAbilityPlanner"/>,
    /// <see cref="ThreadDungeonSpawner.ApplyCreatureAbilities"/>): the budget is the MAX predicted withheld rating over the
    /// family's drawn members, boss included; a clamped family puts the same spec on every member, an unclamped or solo one
    /// none; the draw is a pure function of the run seed; the overlay composes onto authored effects; the attach sits after
    /// the ceiling and before the weak point.
    ///
    /// PropertyManager: ClassInitialize loads the compiled defaults (creature construction and the overlay read
    /// monster_effects_enabled and monster_effect_max_per_monster). Only the monster-effect-switch test changes a key, and it
    /// restores it in a finally block. Selection itself reads <see cref="CreatureAbilitySettings.Defaults"/>, no key.
    /// </summary>
    [TestClass]
    public class FamilyAbilityPlannerTests
    {
        private const string Spawner = "Source/ACE.Server/ThreadDungeons/ThreadDungeonSpawner.cs";
        private const string Signature = "internal static void PrepareForPlacement(ThreadDungeonRun run, Creature creature, DungeonSpawnPlanEntry entry, DungeonSpawnPlan plan,";
        private const int GemLevel = 410;
        private const uint Strength = 200;

        private static uint nextWcid = 0x7F0E8001;
        private static uint nextGuid = 0x7F0E0001;

        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();
        }

        // ---- fixture ---------------------------------------------------------------------------------------

        /// <summary>
        /// G for a 6-player roster at the dials these scenarios were built around (15 per extra member, cap 300): 75. Pinned
        /// rather than read from the shipped defaults so a tuning of the group damage tunables (2026-11-26: 5 per member,
        /// cap 100) does not change the planner scenarios, which exercise the budget logic at a fixed G.
        /// </summary>
        private static int SixPlayerBonus => GroupScaling.DamageRatingBonusFor(6, 15, 300);

        /// <summary>A body-only melee read with the given base damage at Strength 200 (one attribute mod for every member).</summary>
        private static CreaturePerHit Melee(double baseDamage, int damageRating = 0)
            => new CreaturePerHit(GemLevel, new PerHitSource(baseDamage, PropertyAttribute.Strength, Strength, false, isBody: true), default,
                new ArchetypeUpliftInputs(baseDamage, damageRating, null));

        private static double MedianOf(double baseDamage) => Melee(baseDamage).MeleePerHit;

        private static DungeonSpawnPlan Plan(string family, int groupBonus, params (uint Wcid, DungeonRole Role)[] entries)
        {
            var plan = new DungeonSpawnPlan
            {
                FamilyId = family,
                PerHitMedian = MedianOf(1000),
                DamageCeilingCap = 2.0,
                DamageCeilingTop = 3.0,
                GroupDamageRatingBonus = groupBonus,
                AbilityGroupRatingBonus = groupBonus,
                BossNormalize = false,
            };

            foreach (var (wcid, role) in entries)
                plan.Entries.Add(new DungeonSpawnPlanEntry(wcid, role, null));

            return plan;
        }

        /// <summary>A family whose trash member hits 2.5x the median (clamped), with a soft elite and a soft boss.</summary>
        private static (DungeonSpawnPlan Plan, Dictionary<uint, CreaturePerHit> Reads) Clamped(int groupBonus)
        {
            var hard = nextWcid++;
            var elite = nextWcid++;
            var boss = nextWcid++;

            var plan = Plan("fam_clamped", groupBonus, (hard, DungeonRole.Trash), (hard, DungeonRole.Trash), (elite, DungeonRole.Elite), (boss, DungeonRole.Boss));
            var reads = new Dictionary<uint, CreaturePerHit> { [hard] = Melee(2500), [elite] = Melee(500), [boss] = Melee(800) };

            return (plan, reads);
        }

        private static FamilyAbilityPlan Resolve(DungeonSpawnPlan plan, Dictionary<uint, CreaturePerHit> reads, long seed = 42,
            Func<uint, AbilityMemberTraits> traitsOf = null, CreatureAbilitySettings settings = null)
            => FamilyAbilityPlanner.Resolve(plan, w => reads.TryGetValue(w, out var r) ? r : CreaturePerHit.Empty, traitsOf, true,
                settings ?? CreatureAbilitySettings.Defaults, seed);

        private static int ExpectedWithheld(DungeonSpawnPlan plan, double perHit, int ownRating)
            => DamageCeiling.WithheldRating(DamageCeiling.TargetPerHit(perHit, plan.PerHitMedian, plan.DamageCeilingCap, plan.DamageCeilingTop),
                plan.PerHitMedian, plan.DamageCeilingCap, ownRating, plan.AbilityGroupRatingBonus);

        /// <summary>A family with a 1.6x trash hitter and a 3.5x boss.</summary>
        private static (DungeonSpawnPlan Plan, Dictionary<uint, CreaturePerHit> Reads) Mixed(int groupBonus)
        {
            var trash = nextWcid++;
            var boss = nextWcid++;
            var plan = Plan("fam_mixed", groupBonus, (trash, DungeonRole.Trash), (trash, DungeonRole.Trash), (boss, DungeonRole.Boss));
            var reads = new Dictionary<uint, CreaturePerHit> { [trash] = Melee(1600), [boss] = Melee(3500) };
            return (plan, reads);
        }

        // ---- 1. a clamped family: one spec on every member; an unclamped family: none -------------------------

        /// <summary>
        /// A plan carrying the group ratings GroupScaling computes at the SHIPPED dials, the two fields exactly as
        /// DungeonPopulationBuilder.Build copies them (damage on GroupDamageRatingBonus, the budget's own on AbilityGroupRatingBonus).
        /// </summary>
        private static void CarryShippedGroup(DungeonSpawnPlan plan, int roster)
        {
            var group = GroupScaling.Compute(roster, GroupScaling.DefaultEffortPerMember, GroupScaling.DefaultCountShare, GroupScaling.DefaultGroupMaxMonsters,
                GroupScaling.DefaultDamageRatingPerMember, GroupScaling.DefaultDamageRatingCap, GroupScaling.DefaultRewardBonusPerMember,
                GroupScaling.DefaultRewardBonusCap, 2.7);

            plan.GroupDamageRatingBonus = group.DamageRatingBonus;
            plan.AbilityGroupRatingBonus = group.AbilityRatingBonus;
        }

        /// <summary>
        /// GOLDENS from the pre-2026-11-26 planner (origin/master before #2374, damage dials 15 per member, cap 300): the same fixtures
        /// run with GroupDamageRatingBonus = min(15 x (N - 1), 300) through the unchanged planner at seed 42. Owner ruling 2026-11-26:
        /// no impact to family abilities, so the shipped dials (damage 5/100, ability 15/300) must reproduce every row. Kinds are the
        /// chosen abilities in draw order.
        /// </summary>
        private static readonly (int Roster, int OldG, int BudgetClamped, string KindsClamped, int BudgetMixed, string KindsMixed)[] OldGoldens =
        {
            (2, 15, 3, "", 15, "Riposte"),
            (3, 30, 18, "Execute", 30, "Riposte"),
            (6, 75, 63, "Execute,Riposte", 75, "Riposte,Leech"),
            (10, 135, 123, "Execute,Riposte,Speed", 135, "Riposte,Leech,Execute"),
            (20, 285, 273, "Execute,Riposte,Speed,Scattering", 285, "Riposte,Leech,Execute,Scattering"),
        };

        [TestMethod]
        public void At_the_shipped_dials_the_family_budget_and_abilities_equal_what_they_were_before_the_damage_rating_cut()
        {
            foreach (var row in OldGoldens)
            {
                var (clamped, clampedReads) = Clamped(0);
                CarryShippedGroup(clamped, row.Roster);
                var (mixed, mixedReads) = Mixed(0);
                CarryShippedGroup(mixed, row.Roster);

                Assert.AreEqual(row.OldG, clamped.AbilityGroupRatingBonus, $"guard: N={row.Roster} ability G is the old damage G");
                Assert.IsTrue(clamped.GroupDamageRatingBonus < clamped.AbilityGroupRatingBonus, $"guard: N={row.Roster} the damage G is the lower one");

                var a = Resolve(clamped, clampedReads);
                var b = Resolve(mixed, mixedReads);

                Assert.AreEqual(row.BudgetClamped, a.Budget, $"N={row.Roster} clamped family budget");
                Assert.AreEqual(row.KindsClamped, string.Join(",", a.Result.Kinds), $"N={row.Roster} clamped family abilities");
                Assert.AreEqual(row.BudgetMixed, b.Budget, $"N={row.Roster} mixed family budget");
                Assert.AreEqual(row.KindsMixed, string.Join(",", b.Result.Kinds), $"N={row.Roster} mixed family abilities");
            }
        }

        [TestMethod]
        public void The_spawner_resolves_abilities_on_the_ability_rating_not_the_damage_rating()
        {
            // A damage G alone buys nothing (the guard returns before the world cache is touched).
            var (plan, reads) = Clamped(0);
            plan.GroupDamageRatingBonus = 25;
            Assert.IsNull(ThreadDungeonSpawner.ResolveCreatureAbilities(plan, w => reads[w], null, true, 42), "a damage G alone buys nothing");

            // Resolving a family reads each member's traits off the world cache, so the guard and the log line are pinned in source.
            var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read(Spawner),
                "internal static FamilyAbilityPlan ResolveCreatureAbilities(DungeonSpawnPlan plan, Func<uint, CreaturePerHit> perHitOf,");
            StringAssert.Contains(body, "plan.AbilityGroupRatingBonus <= 0");
            Assert.IsFalse(body.Contains("GroupDamageRatingBonus"), "the ability path never reads the damage rating");
        }

        /// <summary>An archetype uplift whose spell-parity top-up is positive (run spell output 320 over the source's 200).</summary>
        private static ArchetypeUpliftInfo ToppedUpInfo(uint wcid)
        {
            DungeonBandStandard Std(uint scale) => DungeonBandStandard.ForTest(null, 20 * scale, 100 * scale,
                attributeMedians: new Dictionary<PropertyAttribute, uint> { [PropertyAttribute.Strength] = 100 * scale, [PropertyAttribute.Coordination] = 100 * scale });

            return new ArchetypeUpliftInfo
            {
                Wcid = wcid,
                RunLevel = 400,
                OwnLevel = 200,
                Run = new ArchetypeAxes(Std(2), 40, 320, 0, 0, 0, 0, 0, 8, 1.0),
                Own = new ArchetypeAxes(Std(1), 20, 200, 0, 0, 0, 0, 0, 8, 1.0),
                OffenseRatio = 1.0,
                DefenseRatio = 1.0,
                CapRatio = 0,
            };
        }

        private static (DungeonSpawnPlan Plan, CreaturePerHit Raw, DungeonSpawnPlanEntry Entry) ToppedUpFamily(double median, int damageG, int abilityG)
        {
            var info = ToppedUpInfo(77);
            var plan = new DungeonSpawnPlan
            {
                FamilyId = "fam_topped",
                PerHitMedian = median,
                DamageCeilingCap = 2.0,
                DamageCeilingTop = 3.0,
                GroupDamageRatingBonus = damageG,
                AbilityGroupRatingBonus = abilityG,
                BossNormalize = false,
                ArchetypeUplift = ArchetypeUpliftContext.ForTest(400, info),
            };
            var entry = new DungeonSpawnPlanEntry(77, DungeonRole.Trash, null, 400);
            plan.Entries.Add(entry);

            var raw = new CreaturePerHit(200, new PerHitSource(30, PropertyAttribute.Strength, 100, false, isBody: true), default,
                new ArchetypeUpliftInputs(30, 0, new[] { 200.0 }));

            return (plan, raw, entry);
        }

        [TestMethod]
        public void The_archetype_prediction_and_budget_read_the_ability_rating_not_the_damage_rating()
        {
            // GOLDENS: the pre-change planner (origin/master, which fed the damage G into the archetype uplift's rating offset)
            // at the old dials, where damage G == ability G == 75.
            foreach (var (median, golden) in new[] { (50.0, 47), (60.0, 24) })
            {
                var (lowDamage, raw, entry) = ToppedUpFamily(median, 25, 75);
                var (sameDamage, _, entry2) = ToppedUpFamily(median, 75, 75);

                Assert.IsTrue(ThreadDungeonSpawner.ArchetypeInfoFor(entry, lowDamage) != null, "guard: the entry is archetype-uplifted");
                var probe = lowDamage.ArchetypeUplift.For(77);
                Assert.IsTrue(ArchetypeUplift.DamageRatingFloor(probe, 0, raw.UpliftInputs.SpellMeanAt(probe.Run.Core.SpellTier)).Added > 0, "guard: a spell-parity top-up is in play");

                var low = FamilyAbilityPlanner.PredictEntry(lowDamage, entry, raw, true);
                var same = FamilyAbilityPlanner.PredictEntry(sameDamage, entry2, raw, true);

                Assert.AreEqual(same.MeleePerHit, low.MeleePerHit, 0.0, $"median {median}: the predicted per-hit does not depend on the damage G");
                Assert.AreEqual(golden, Resolve(sameDamage, new Dictionary<uint, CreaturePerHit> { [77] = raw }).Budget, $"median {median}: golden budget");
                Assert.AreEqual(golden, Resolve(lowDamage, new Dictionary<uint, CreaturePerHit> { [77] = raw }).Budget, $"median {median}: budget at damage G 25");
            }
        }

        [TestMethod]
        public void The_budget_reads_the_ability_rating_and_ignores_the_damage_rating()
        {
            var (plan, reads) = Clamped(75);
            var baseline = Resolve(plan, reads);

            plan.GroupDamageRatingBonus = 25;
            Assert.AreEqual(baseline.Budget, Resolve(plan, reads).Budget, "lowering the damage G does not move the budget");
            Assert.AreEqual(63, baseline.Budget, "guard: the golden six-player budget");

            plan.GroupDamageRatingBonus = 75;
            plan.AbilityGroupRatingBonus = 25;
            Assert.IsTrue(Resolve(plan, reads).Budget <= 25, "the budget is bounded by the ability G, not the damage G");

            plan.AbilityGroupRatingBonus = 0;
            Assert.AreEqual(0, Resolve(plan, reads).Budget, "a plan with no ability G buys nothing even with a damage G");
        }

        [TestMethod]
        public void A_six_player_plan_at_410_gives_every_member_of_a_clamped_family_the_same_spec()
        {
            var (plan, reads) = Clamped(SixPlayerBonus);
            var hard = plan.Entries[0].Wcid;

            var abilities = Resolve(plan, reads);

            Assert.AreEqual(75, plan.GroupDamageRatingBonus, "guard: the pinned 6-player group bonus");
            Assert.AreEqual(3, abilities.Members.Count, "one member per distinct drawn (wcid, role), the boss included");
            Assert.AreEqual(ExpectedWithheld(plan, reads[hard].MeleePerHit, 0), abilities.Budget, "the budget is the hardest hitter's withheld rating");
            Assert.IsTrue(abilities.Budget >= 45, $"guard: a 2.5x hitter at G 75 withholds at least two tiers (budget {abilities.Budget})");
            Assert.AreEqual(ThreadCreatureAbilities.AbilityCount(CreatureAbilitySettings.DefaultTiers, abilities.Budget), abilities.Result.Kinds.Count);
            Assert.IsFalse(string.IsNullOrEmpty(abilities.Result.Spec));

            plan.CreatureAbilities = abilities;

            var attachedSets = new List<string>();

            foreach (var entry in plan.Entries)
            {
                var creature = TestCreatures.CreateEffectCarrier(null, entry.Wcid);

                Assert.IsTrue(ThreadDungeonSpawner.ApplyCreatureAbilities(null, creature, entry, plan), $"{entry.Role} {entry.Wcid} got the overlay");

                var (kinds, count) = creature.GetAttachedMonsterEffectKinds();
                attachedSets.Add(string.Join(",", kinds.OrderBy(k => k, StringComparer.Ordinal)) + $"#{count}");
            }

            Assert.AreEqual(1, attachedSets.Distinct().Count(), "every member, trash, elite and boss, carries the same set: " + string.Join(" | ", attachedSets));
        }

        [TestMethod]
        public void An_unclamped_family_in_the_same_group_buys_nothing_and_attaches_nothing()
        {
            var soft = nextWcid++;
            var boss = nextWcid++;
            var plan = Plan("fam_soft", SixPlayerBonus, (soft, DungeonRole.Trash), (boss, DungeonRole.Boss));
            var reads = new Dictionary<uint, CreaturePerHit> { [soft] = Melee(500), [boss] = Melee(900) };

            var abilities = Resolve(plan, reads);

            Assert.AreEqual(0, abilities.Budget, "a family that never reaches the solo ceiling under G withholds nothing");
            Assert.AreSame(CreatureAbilityResult.Empty, abilities.Result);

            plan.CreatureAbilities = abilities;

            var creature = TestCreatures.CreateEffectCarrier(null, soft);
            Assert.IsFalse(ThreadDungeonSpawner.ApplyCreatureAbilities(null, creature, plan.Entries[0], plan));
            Assert.IsNull(creature.MonsterEffects);
        }

        [TestMethod]
        public void The_boss_counts_toward_the_family_budget()
        {
            var soft = nextWcid++;
            var boss = nextWcid++;
            var plan = Plan("fam_boss", SixPlayerBonus, (soft, DungeonRole.Trash), (boss, DungeonRole.Boss));
            plan.BossDamageRating = 10;
            var reads = new Dictionary<uint, CreaturePerHit> { [soft] = Melee(500), [boss] = Melee(2800) };

            var abilities = Resolve(plan, reads);

            var bossMember = abilities.Members.Single(m => m.Role == DungeonRole.Boss);
            Assert.AreEqual(10, bossMember.OwnRating, "R = the gem's boss rating (authored 0, not normalized)");
            Assert.AreEqual(ExpectedWithheld(plan, reads[boss].MeleePerHit, 10), abilities.Budget);
            Assert.IsTrue(abilities.Budget > 0);
            Assert.AreEqual(abilities.Budget, bossMember.Withheld, "the boss alone sets the budget");
        }

        // ---- 2. solo ----------------------------------------------------------------------------------------

        [TestMethod]
        public void A_solo_plan_attaches_nothing()
        {
            var (plan, reads) = Clamped(0);

            var abilities = Resolve(plan, reads);

            Assert.AreEqual(0, abilities.Budget);
            Assert.AreSame(CreatureAbilityResult.Empty, abilities.Result);
            Assert.IsNull(ThreadDungeonSpawner.ResolveCreatureAbilities(plan, w => reads[w], null, true, 42), "the spawner resolves nothing for G 0");

            plan.CreatureAbilities = abilities;
            var creature = TestCreatures.CreateEffectCarrier(null, plan.Entries[0].Wcid);
            Assert.IsFalse(ThreadDungeonSpawner.ApplyCreatureAbilities(null, creature, plan.Entries[0], plan));
        }

        [TestMethod]
        public void A_plan_carrying_no_abilities_attaches_nothing()
        {
            var (plan, _) = Clamped(SixPlayerBonus);
            var creature = TestCreatures.CreateEffectCarrier(null, plan.Entries[0].Wcid);

            Assert.IsNull(plan.CreatureAbilities, "Build never sets it (speed courses, the offline harness)");
            Assert.IsFalse(ThreadDungeonSpawner.ApplyCreatureAbilities(null, creature, plan.Entries[0], plan));
            Assert.IsNull(creature.MonsterEffects);
        }

        // ---- 3. determinism ---------------------------------------------------------------------------------

        [TestMethod]
        public void The_same_seed_gives_the_same_specs_and_takes_no_draw_from_a_shared_rng()
        {
            var (plan, reads) = Clamped(SixPlayerBonus);

            var first = Resolve(plan, reads, seed: 1234);
            var second = Resolve(plan, reads, seed: 1234);

            Assert.AreEqual(first.Result.Spec, second.Result.Spec);
            CollectionAssert.AreEqual(first.Result.Kinds.ToList(), second.Result.Kinds.ToList());
            Assert.AreEqual(ThreadCreatureAbilities.Select(CreatureAbilitySettings.Defaults, 1234, "fam_clamped", first.Budget, first.Facts).Spec, first.Result.Spec,
                "exactly the step-5 selection, keyed by the run seed and the family id");

            // Across seeds the draw moves (the guard that the seed is actually read).
            var specs = Enumerable.Range(1, 40).Select(s => Resolve(plan, reads, seed: s).Result.Spec).Distinct().Count();
            Assert.IsTrue(specs > 1, "different run seeds draw different sets");
        }

        // ---- 4. monster_effects_enabled off -----------------------------------------------------------------

        [TestMethod]
        public void Monster_effects_off_selects_nothing_and_the_ceiling_still_runs()
        {
            var (plan, reads) = Clamped(SixPlayerBonus);

            try
            {
                Assert.IsTrue(PropertyManager.ModifyBool("monster_effects_enabled", false));

                var settings = CreatureAbilitySettings.Read();
                Assert.IsFalse(settings.Enabled);

                var abilities = Resolve(plan, reads, settings: settings);

                Assert.IsTrue(abilities.Budget > 0, "the budget is still read");
                Assert.AreSame(CreatureAbilityResult.Empty, abilities.Result, "but nothing is selected");

                plan.CreatureAbilities = abilities;
                var creature = TestCreatures.CreateEffectCarrier(null, plan.Entries[0].Wcid);
                Assert.IsFalse(ThreadDungeonSpawner.ApplyCreatureAbilities(null, creature, plan.Entries[0], plan));

                // The ceiling is independent of the switch: a hand-built creature hitting 2.5x the median is still cut.
                var hitter = CeilingCreature(2500);
                var result = ThreadDungeonSpawner.ApplyDamageCeiling(null, hitter, plan.Entries[0], plan);
                Assert.IsNotNull(result);
                Assert.IsTrue(result.MeleeTarget < result.MeleeBefore, "the ceiling still compresses");
            }
            finally
            {
                PropertyManager.ModifyBool("monster_effects_enabled", true);
            }
        }

        // ---- 5. composition with authored effects -----------------------------------------------------------

        [TestMethod]
        public void The_overlay_composes_onto_authored_effects_and_never_doubles_an_authored_kind()
        {
            const string Authored = "leech vital=health pct=0.12";

            var (plan, reads) = Clamped(SixPlayerBonus);
            var traits = AbilityMemberTraits.FromWeenie(new Weenie
            {
                PropertiesString = new Dictionary<PropertyString, string> { [PropertyString.MonsterCombatEffects] = Authored },
            }, casts: false);

            CollectionAssert.AreEqual(new[] { "leech" }, traits.AuthoredKinds.ToList());
            Assert.AreEqual(1, traits.AuthoredRecordCount);

            var abilities = Resolve(plan, reads, traitsOf: _ => traits);

            Assert.IsTrue(abilities.Facts.AuthoredEffectKinds.Contains("leech"));
            Assert.AreEqual(1, abilities.Facts.AuthoredRecordCount);
            CollectionAssert.DoesNotContain(abilities.Result.Kinds.ToList(), CreatureAbilityKind.Leech, "an authored kind is excluded");
            Assert.IsTrue(abilities.Result.Kinds.Count > 0);

            plan.CreatureAbilities = abilities;

            var creature = TestCreatures.CreateEffectCarrier(Authored, plan.Entries[0].Wcid);
            Assert.AreEqual(1, creature.MonsterEffects.Count);

            Assert.IsTrue(ThreadDungeonSpawner.ApplyCreatureAbilities(null, creature, plan.Entries[0], plan));

            var (kinds, count) = creature.GetAttachedMonsterEffectKinds();
            Assert.IsTrue(kinds.Contains("leech"), "the authored leech survives");
            var records = abilities.Result.Spec.Split(';').Count(r => !string.IsNullOrWhiteSpace(r));
            Assert.AreEqual(1 + records, count, "authored record plus every overlaid record");
        }

        // ---- 5b. style: the union decides eligibility, the budget setter the weighting -------------------------

        [TestMethod]
        public void A_family_whose_budget_a_pure_melee_member_sets_favours_riposte_and_execute()
        {
            var melee = nextWcid++;
            var caster = nextWcid++;
            var plan = Plan("fam_style", SixPlayerBonus, (melee, DungeonRole.Trash), (caster, DungeonRole.Elite));
            var reads = new Dictionary<uint, CreaturePerHit> { [melee] = Melee(2500), [caster] = Melee(400) };
            AbilityMemberTraits TraitsOf(uint w) => new AbilityMemberTraits(w == caster, null, 0);

            var abilities = Resolve(plan, reads, traitsOf: TraitsOf);

            Assert.AreEqual(melee, abilities.BudgetSetterMember.Wcid, "the 2.5x melee hitter sets the budget");
            Assert.IsTrue(abilities.Facts.Melee && abilities.Facts.Caster, "eligibility is the union: a pure melee and a caster (cast share 1, so melee is no mode of its)");

            var weighting = abilities.Facts.Weighting;
            Assert.IsNotNull(weighting);
            Assert.IsTrue(weighting.Melee);
            Assert.IsFalse(weighting.Caster || weighting.Hybrid || weighting.Missile, "weighted by the setter alone");

            // Over many run seeds: the setter's weighting draws riposte/execute more often than the union weighting would, and
            // the caster-only kinds the union made eligible still appear.
            int favoured = 0, unionFavoured = 0, casterOnly = 0;

            for (var seed = 1; seed <= 400; seed++)
            {
                var kinds = Resolve(plan, reads, seed: seed, traitsOf: TraitsOf).Result.Kinds;
                favoured += kinds.Count(k => k == CreatureAbilityKind.Riposte || k == CreatureAbilityKind.Execute);
                casterOnly += kinds.Count(k => k == CreatureAbilityKind.FrenzyCast || k == CreatureAbilityKind.SpeedCast || k == CreatureAbilityKind.Draining);

                var unionFacts = abilities.Facts;
                var union = ThreadCreatureAbilities.Select(CreatureAbilitySettings.Defaults, seed, "fam_style", abilities.Budget, new CreatureAbilityFacts
                {
                    Melee = unionFacts.Melee, Missile = unionFacts.Missile, Caster = unionFacts.Caster, Hybrid = unionFacts.Hybrid,
                    AuthoredEffectKinds = unionFacts.AuthoredEffectKinds, AuthoredRecordCount = unionFacts.AuthoredRecordCount, BandSpellTier = unionFacts.BandSpellTier,
                });
                unionFavoured += union.Kinds.Count(k => k == CreatureAbilityKind.Riposte || k == CreatureAbilityKind.Execute);
            }

            Assert.IsTrue(favoured > unionFavoured, $"setter weighting favours riposte/execute more than the union does ({favoured} vs {unionFavoured})");
            Assert.IsTrue(favoured > casterOnly, $"riposte/execute outdraw the three caster-only kinds ({favoured} vs {casterOnly})");
            Assert.IsTrue(casterOnly > 0, "the caster-only kinds stay eligible through the union");
        }

        [TestMethod]
        public void A_setter_weights_by_its_cast_share_against_the_classifier_thresholds()
        {
            FacetsOf(0.0, melee: true, out var none);
            Assert.IsTrue(none.Melee && !none.Hybrid && !none.Caster, "no casting: melee");

            FacetsOf(ArchetypeClassifier.HybridFloor - 0.01, melee: true, out var incidental);
            Assert.IsTrue(incidental.Melee && !incidental.Hybrid && !incidental.Caster, "casting below HybridFloor is incidental: melee");

            FacetsOf(ArchetypeClassifier.HybridFloor, melee: true, out var hybrid);
            Assert.IsTrue(hybrid.Hybrid && !hybrid.Melee && !hybrid.Caster, "a recognisable casting mode beside melee: hybrid");

            FacetsOf(ArchetypeClassifier.HybridFloor, melee: false, out var lightCaster);
            Assert.IsTrue(lightCaster.Caster && !lightCaster.Hybrid && !lightCaster.Melee, "casting mode with no melee: caster");

            FacetsOf(ArchetypeClassifier.CasterShare, melee: true, out var caster);
            Assert.IsTrue(caster.Caster && !caster.Hybrid && !caster.Melee, "at CasterShare the classifier calls it a caster even when it melees");

            static void FacetsOf(double share, bool melee, out CreatureAbilityFacts style)
                => style = new FamilyAbilityMember(1, DungeonRole.Elite, 0, melee ? 5000 : 0, 0, 75, 0, casts: share > 0, castShare: share).WeightStyle();
        }

        [TestMethod]
        public void A_melee_setter_with_an_incidental_spell_book_weights_as_melee_while_its_spells_stay_eligible()
        {
            var setter = nextWcid++;
            var plan = Plan("fam_incidental", SixPlayerBonus, (setter, DungeonRole.Elite));
            var reads = new Dictionary<uint, CreaturePerHit> { [setter] = Melee(2500) };

            var abilities = Resolve(plan, reads, traitsOf: _ => new AbilityMemberTraits(true, null, 0, 0.18));

            Assert.IsTrue(abilities.Facts.Hybrid, "eligibility: it melees and has a harmful spell, so the caster kinds are eligible");
            Assert.IsTrue(abilities.Facts.Weighting.Melee && !abilities.Facts.Weighting.Hybrid, "weighting: 0.18 is below HybridFloor, so melee");
        }

        // ---- code review 2026-10-26 F1, F3, F6, F7 ---------------------------------------------------------------

        [TestMethod]
        public void The_spec_is_canonical_so_one_kind_set_is_one_string_whatever_the_draw_order()
        {
            var facts = new CreatureAbilityFacts { Melee = true, Caster = true, BandSpellTier = 6 };
            var bySet = new Dictionary<string, (HashSet<string> Specs, HashSet<string> Orders)>();

            for (var seed = 1; seed <= 600; seed++)
            {
                var result = ThreadCreatureAbilities.Select(CreatureAbilitySettings.Defaults, seed, "fam_canon", 300, facts);

                // Draining's spell is itself seeded, so a set holding it can legitimately differ; every other set must not.
                if (result.Kinds.Contains(CreatureAbilityKind.Draining))
                    continue;

                var set = string.Join(",", result.Kinds.OrderBy(k => (int)k));
                if (!bySet.TryGetValue(set, out var seen))
                    bySet[set] = seen = (new HashSet<string>(), new HashSet<string>());

                seen.Specs.Add(result.Spec);
                seen.Orders.Add(string.Join(",", result.Kinds));

                // Records in enum order, riposte's avoid+riposte adjacent.
                var kinds = result.Spec.Split(';').Select(r => r.Trim().Split(' ')[0]).ToList();
                var expected = result.Kinds.OrderBy(k => (int)k).SelectMany(k => k == CreatureAbilityKind.Riposte ? new[] { "avoid", "riposte" } : new[] { KindToken(k) }).ToList();
                CollectionAssert.AreEqual(expected, kinds, $"seed {seed}: records in canonical order");
            }

            Assert.IsTrue(bySet.Values.Any(v => v.Orders.Count > 1), "guard: some set was drawn in more than one order");

            foreach (var kv in bySet)
                Assert.AreEqual(1, kv.Value.Specs.Count, $"set {kv.Key}: one spec string for {kv.Value.Orders.Count} draw order(s)");

            static string KindToken(CreatureAbilityKind k) => k switch
            {
                CreatureAbilityKind.Execute => "execute",
                CreatureAbilityKind.Leech => "leech",
                CreatureAbilityKind.Frenzy or CreatureAbilityKind.FrenzyCast => "ramp",
                CreatureAbilityKind.Speed or CreatureAbilityKind.SpeedCast => "speed",
                CreatureAbilityKind.Draining => "castspell",
                _ => "scatter",
            };
        }

        [TestMethod]
        public void The_strip_key_is_passed_when_the_archetype_uplift_or_the_abilities_read_the_profiles()
        {
            var strip = SpellStripConfig.Default;

            Assert.AreEqual(strip.Key, ThreadDungeonSpawner.StripKeyFor(true, false, strip));
            Assert.AreEqual(strip.Key, ThreadDungeonSpawner.StripKeyFor(false, true, strip), "the abilities' cast-share weighting reads the post-strip book");
            Assert.AreEqual(strip.Key, ThreadDungeonSpawner.StripKeyFor(true, true, strip));
            Assert.IsNull(ThreadDungeonSpawner.StripKeyFor(false, false, strip), "nothing reads it: a strip dial change must not drop the cache");

            var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read(Spawner), "internal static DungeonSpawnPlan BuildLivePlan(");
            StringAssert.Contains(body, "StripKeyFor(archetypeUpliftOn, creatureAbilitiesOn, spellStrip)", "BuildLivePlan keys the memo through it");
        }

        [TestMethod]
        public void An_all_caster_family_is_not_riposte_eligible()
        {
            var a = nextWcid++;
            var b = nextWcid++;
            var plan = Plan("fam_casters", SixPlayerBonus, (a, DungeonRole.Trash), (b, DungeonRole.Elite));
            var reads = new Dictionary<uint, CreaturePerHit> { [a] = Melee(2500), [b] = Melee(900) };

            // Both swing now and then (a melee read exists) but cast on 85% of attacks: melee mix 0.15, below HybridFloor and not
            // their largest mode, so melee is no mode of theirs.
            var abilities = Resolve(plan, reads, traitsOf: _ => new AbilityMemberTraits(true, null, 0, 0.85));

            Assert.IsTrue(abilities.Facts.Caster);
            Assert.IsFalse(abilities.Facts.Melee || abilities.Facts.Hybrid, "no member melees as a mode");

            for (var seed = 1; seed <= 200; seed++)
                CollectionAssert.DoesNotContain(Resolve(plan, reads, seed: seed, traitsOf: _ => new AbilityMemberTraits(true, null, 0, 0.85)).Result.Kinds.ToList(),
                    CreatureAbilityKind.Riposte, $"seed {seed}");

            // Control: the same family at an incidental cast share melees, and riposte is drawn for some seed.
            var meleeSeeds = Enumerable.Range(1, 200).Count(s => Resolve(plan, reads, seed: s, traitsOf: _ => new AbilityMemberTraits(true, null, 0, 0.10)).Result.Kinds.Contains(CreatureAbilityKind.Riposte));
            Assert.IsTrue(meleeSeeds > 0, "control: a melee family does draw riposte");
        }

        [TestMethod]
        public void A_plan_with_no_family_keys_its_draw_as_none()
        {
            var (plan, reads) = Clamped(SixPlayerBonus);
            plan.FamilyId = null;

            var abilities = Resolve(plan, reads, seed: 77);

            Assert.AreEqual("none", abilities.FamilyKey);
            Assert.AreEqual(ThreadCreatureAbilities.Select(CreatureAbilitySettings.Defaults, 77, "none", abilities.Budget, abilities.Facts).Spec, abilities.Result.Spec);
            Assert.IsFalse(string.IsNullOrEmpty(abilities.Result.Spec), "a family-less plan still buys abilities");
        }

        [TestMethod]
        public void A_gear_unknown_member_is_counted_and_still_budgets_on_its_prediction()
        {
            var unknown = nextWcid++;
            var known = nextWcid++;
            var plan = Plan("fam_gear", SixPlayerBonus, (unknown, DungeonRole.Trash), (known, DungeonRole.Trash));
            var hard = Melee(2500);
            var reads = new Dictionary<uint, CreaturePerHit>
            {
                [unknown] = new CreaturePerHit(hard.Level, hard.Melee, hard.Missile, hard.UpliftInputs, gearUnknown: true),
                [known] = Melee(500),
            };

            var abilities = Resolve(plan, reads);

            Assert.AreEqual(1, abilities.GearUnknownCount);
            Assert.IsTrue(abilities.Members.Single(m => m.Wcid == unknown).GearUnknown);
            Assert.AreEqual(unknown, abilities.BudgetSetterMember.Wcid, "the budget rule is unchanged: its prediction (a floor) still counts");
            Assert.AreEqual(ExpectedWithheld(plan, hard.MeleePerHit, 0), abilities.Budget);
        }

        [TestMethod]
        public void The_budget_setter_breaks_a_withheld_tie_by_the_larger_hit()
        {
            var smaller = new FamilyAbilityMember(1, DungeonRole.Trash, 0, 3000, 0, 75, 0);
            var larger = new FamilyAbilityMember(2, DungeonRole.Elite, 0, 0, 5000, 0, 75, casts: true);
            var lower = new FamilyAbilityMember(3, DungeonRole.Boss, 0, 9000, 0, 60, 0);

            Assert.AreSame(larger, FamilyAbilityPlanner.BudgetSetter(new[] { smaller, larger, lower }));
            Assert.AreSame(larger, FamilyAbilityPlanner.BudgetSetter(new[] { larger, smaller, lower }));
            Assert.AreSame(smaller, FamilyAbilityPlanner.BudgetSetter(new[] { smaller, new FamilyAbilityMember(4, DungeonRole.Trash, 0, 3000, 0, 75, 0) }), "then the first drawn");
            Assert.IsNull(FamilyAbilityPlanner.BudgetSetter(new FamilyAbilityMember[0]));

            var style = larger.Style();
            Assert.IsTrue(style.Missile && style.Caster && !style.Melee && !style.Hybrid, "a shooting caster with no melee");
        }

        // ---- 6. the prediction ------------------------------------------------------------------------------

        [TestMethod]
        public void A_normalized_boss_is_predicted_as_the_normalizer_fields_it()
        {
            TestGameTables.EnsureInitialized();

            var weenie = BossWeenie(strength: 450, dval: 900);
            var standard = DungeonBandStandard.ForTest(new Dictionary<Skill, uint>(), maxBodyDamage: 400, maxBaseArmor: 300,
                attributeMedians: new Dictionary<PropertyAttribute, uint> { [PropertyAttribute.Strength] = 280, [PropertyAttribute.Endurance] = 250 });

            var plan = new DungeonSpawnPlan
            {
                FamilyId = "fam_norm",
                BossNormalize = true,
                BandStandard = standard,
                BossOffenseBandRatio = 1.25,
                BossDefenseBandRatio = 1.0,
                BossDamageRating = 20,
            };
            var entry = new DungeonSpawnPlanEntry(weenie.WeenieClassId, DungeonRole.Boss, null);
            var raw = CreaturePerHit.FromWeenie(weenie, _ => null);

            var predicted = FamilyAbilityPlanner.PredictEntry(plan, entry, raw, upliftAttributes: true);

            var creature = new Creature(weenie, new ObjectGuid(nextGuid++));
            DungeonCreatureNormalizer.NormalizeBossAttributes(creature, plan);
            DungeonCreatureNormalizer.NormalizeBoss(creature, plan);
            var live = DamageCeiling.Measure(creature);

            Assert.AreNotEqual(raw.MeleePerHit, predicted.MeleePerHit, 1e-6, "guard: normalization moved the prediction");
            Assert.AreEqual(live.Melee, predicted.MeleePerHit, live.Melee * 1e-6, "the prediction matches the normalized creature's measured per-hit");
            Assert.AreEqual(20, FamilyAbilityPlanner.PredictOwnRating(plan, entry, raw), "a normalized boss's authored rating is zeroed; R = the gem's boss rating");
        }

        // ---- 7. source pins ---------------------------------------------------------------------------------

        [TestMethod]
        public void The_abilities_attach_once_after_the_ceiling_and_before_the_weak_point()
        {
            const string Call = "ApplyCreatureAbilities(logTag, creature, entry, plan);";

            var source = PooledLootSourceText.Read(Spawner);
            var body = PooledLootSourceText.MethodBody(source, Signature);

            var call = body.IndexOf(Call, StringComparison.Ordinal);
            Assert.IsTrue(call >= 0, "PrepareForPlacement no longer attaches the abilities");
            Assert.AreEqual(call, body.LastIndexOf(Call, StringComparison.Ordinal), "attached once");
            Assert.IsFalse(body.Contains("ApplyMonsterEffectOverlay("), "the one overlay call lives in ApplyCreatureAbilities");

            var ceiling = body.IndexOf("ApplyDamageCeiling(logTag, creature, entry, plan);", StringComparison.Ordinal);
            var weak = body.IndexOf("ApplyWeakPoint(logTag, creature, entry, plan);", StringComparison.Ordinal);

            Assert.IsTrue(ceiling >= 0 && ceiling < call, "after the ceiling");
            Assert.IsTrue(weak > call, "before the weak point");
            Assert.AreEqual(weak, body.LastIndexOf("ApplyWeakPoint(", StringComparison.Ordinal), "the weak point is still the last writer");

            var attach = PooledLootSourceText.MethodBody(source, "internal static bool ApplyCreatureAbilities(object logTag, Creature creature, DungeonSpawnPlanEntry entry, DungeonSpawnPlan plan)");
            Assert.AreEqual(1, attach.Split("ApplyMonsterEffectOverlay(").Length - 1, "exactly one overlay call");
        }

        [TestMethod]
        public void BuildLivePlan_resolves_the_abilities_once_after_the_plan_is_built()
        {
            var source = PooledLootSourceText.Read(Spawner);
            var body = PooledLootSourceText.MethodBody(source, "internal static DungeonSpawnPlan BuildLivePlan(");

            // TryPopulate goes through BuildLivePlan and must not resolve the abilities a second time itself.
            Assert.IsFalse(PooledLootSourceText.MethodBody(source, "public static void TryPopulate(ThreadDungeonRun run, Landblock landblock)").Contains("ResolveCreatureAbilities("),
                "TryPopulate must not resolve the abilities itself: BuildLivePlan does it, once per run");

            var build = body.IndexOf("DungeonPopulationBuilder.Build(", StringComparison.Ordinal);
            var resolve = body.IndexOf("plan.CreatureAbilities = ResolveCreatureAbilities(", StringComparison.Ordinal);

            Assert.IsTrue(build >= 0 && resolve > build, "resolved after Build");
            Assert.AreEqual(body.IndexOf("ResolveCreatureAbilities(", StringComparison.Ordinal), body.LastIndexOf("ResolveCreatureAbilities(", StringComparison.Ordinal), "once per run");
        }

        // ---- helpers ----------------------------------------------------------------------------------------

        private static Weenie BossWeenie(uint strength, int dval) => new Weenie
        {
            WeenieClassId = nextWcid++,
            ClassName = "abilityboss",
            WeenieType = WeenieType.Creature,
            PropertiesInt = new Dictionary<PropertyInt, int> { [PropertyInt.Level] = GemLevel, [PropertyInt.DamageRating] = 40 },
            PropertiesString = new Dictionary<PropertyString, string> { [PropertyString.Name] = "Ability Boss" },
            PropertiesAttribute = new Dictionary<PropertyAttribute, PropertiesAttribute>
            {
                [PropertyAttribute.Strength] = new PropertiesAttribute { InitLevel = strength },
                [PropertyAttribute.Endurance] = new PropertiesAttribute { InitLevel = 200 },
                [PropertyAttribute.Coordination] = new PropertiesAttribute { InitLevel = 150 },
                [PropertyAttribute.Quickness] = new PropertiesAttribute { InitLevel = 200 },
                [PropertyAttribute.Focus] = new PropertiesAttribute { InitLevel = 200 },
                [PropertyAttribute.Self] = new PropertiesAttribute { InitLevel = 200 },
            },
            PropertiesAttribute2nd = new Dictionary<PropertyAttribute2nd, PropertiesAttribute2nd>
            {
                [PropertyAttribute2nd.MaxHealth] = new PropertiesAttribute2nd { InitLevel = 1000 },
                [PropertyAttribute2nd.MaxStamina] = new PropertiesAttribute2nd { InitLevel = 500 },
                [PropertyAttribute2nd.MaxMana] = new PropertiesAttribute2nd { InitLevel = 500 },
            },
            PropertiesBodyPart = new Dictionary<CombatBodyPart, PropertiesBodyPart>
            {
                [CombatBodyPart.Head] = new PropertiesBodyPart { DType = DamageType.Slash, DVal = dval, DVar = 0.5f, BaseArmor = 300, HLF = 0.1f, HRF = 0.1f, MLF = 0.1f, MRF = 0.1f, LLF = 0.1f, LRF = 0.1f },
                [CombatBodyPart.Hand] = new PropertiesBodyPart { DType = DamageType.Slash, DVal = dval / 2, DVar = 0.5f, BaseArmor = 300, HLF = 0.1f, HRF = 0.1f, MLF = 0.1f, MRF = 0.1f, LLF = 0.1f, LRF = 0.1f },
            },
        };

        /// <summary>A live body-only creature at Strength 200 whose best part is <paramref name="dval"/>.</summary>
        private static Creature CeilingCreature(int dval)
        {
            TestGameTables.EnsureInitialized();

            var weenie = BossWeenie(Strength, dval);
            weenie.PropertiesInt.Remove(PropertyInt.DamageRating);

            return new Creature(weenie, new ObjectGuid(nextGuid++));
        }
    }
}
