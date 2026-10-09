using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

using ACE.Server.Entity;
using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;
using ACE.Server.WorldEvents.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// Group Threads task 6: count, health, damage and XP scaling in the populate path
    /// (Docs/Threads/GROUP-THREADS-DESIGN.md sections 5, 6.1, 6.2; rulings R13, R15, R16, R27).
    ///
    /// Nothing here reads PropertyManager: the builder is pure, GroupScaling.Compute takes its tunables as
    /// arguments, and Fellowship.GetMemberSharePercent is a pure static. The fixtures mirror
    /// DungeonPopulationBuilderTests (private there, so copied).
    /// </summary>
    [TestClass]
    public class GroupPopulationScalingTests
    {
        private static readonly Dictionary<uint, int> Levels = new Dictionary<uint, int> { [100] = 100, [101] = 120, [110] = 130, [10981] = 110 };
        private static int LevelOf(uint w) => Levels[w];
        private static uint HealthOf(uint w) => 0;
        private static uint TrashHealthOf(uint w) => w == 100 ? 4000u : w == 101 ? 6000u : w == 110 ? 9000u : 0u;

        private static DungeonSpawnPointDef Pt(int i) => new DungeonSpawnPointDef { Cell = 0x01500100u + (uint)i, X = i, Y = 0, Z = 0, Clearance = 2, Curated = true };

        private static DungeonEntryDef Dungeon(int points) => new DungeonEntryDef
        {
            Id = "filos_doom", Landblock = 0x0150, Name = "Filos' Doom", ExitPortalWcid = 1003601,
            Points = Enumerable.Range(1, points).Select(i => Pt(i)).ToList(),
            BossAnchor = Pt(99),
            CreatureTypes = new List<string> { "Banderling" },
        };

        private static Dictionary<string, SpeciesTableDef> Species() => new Dictionary<string, SpeciesTableDef>
        {
            ["banderling"] = new SpeciesTableDef
            {
                Id = "banderling", CreatureType = "Banderling",
                Members = new List<SpeciesMemberDef> { new SpeciesMemberDef { Wcid = 100, Role = 0 }, new SpeciesMemberDef { Wcid = 101, Role = 0 }, new SpeciesMemberDef { Wcid = 110, Role = 1 } }
            }
        };

        private static ThreadDungeonStore Store() => ThreadDungeonStore.Parse(
            "{\"dungeons\":[]}",
            "{\"bosses\":[{\"wcid\":10981,\"name\":\"Aun Tanua\",\"level\":110,\"families\":[],\"modifiers\":[\"boss_guarded\"]}]}",
            "{\"modifiers\":[" +
            "{\"id\":\"hardy\",\"target\":\"monster\",\"minMagnitude\":1.3,\"maxMagnitude\":2.0,\"monsterEffectKind\":\"health_mult\",\"rewardXpKind\":\"linear\",\"rewardXpBase\":1.0,\"rewardXpSlope\":0.25}," +
            "{\"id\":\"teeming\",\"target\":\"run\",\"minMagnitude\":1.25,\"maxMagnitude\":1.75,\"monsterEffectKind\":\"count_mult\"}," +
            "{\"id\":\"boss_guarded\",\"target\":\"boss\",\"minMagnitude\":1.5,\"maxMagnitude\":3.0,\"monsterEffectKind\":\"health_mult\"}]," +
            "\"xpLadder\":[{\"level\":20,\"xp\":3500},{\"level\":100,\"xp\":80000},{\"level\":200,\"xp\":1100000}]}",
            new Dictionary<string, string>());

        private static DungeonGemSpec Spec(params (string, double)[] mods) => new DungeonGemSpec("filos_doom", 100, 6, "any", 1, mods, 0, 0);

        /// <summary>
        /// The shipped tunable defaults (global-context tunables table), spelled as literals so a drift in a
        /// compiled constant cannot silently move these expectations with it. Plateau 0 reads as the retail one.
        /// </summary>
        private static GroupScaling Group(int n, long groupMax = 180)
            => GroupScaling.Compute(n, 1.0, 0.5, groupMax, 15, 300, 0.05, 1.5, 0.0);

        /// <summary>40 curated points, floor 40, cap 120: a 40-slot solo base, with scaling and the curve off.</summary>
        private static DungeonPopulationLimits Limits(GroupScaling group = null, int max = 120, int minMonsters = 40)
            => new DungeonPopulationLimits(max, 2.0, 3.0, 8, 0.5, rewardScalingEnabled: false, healthCurveEnabled: false,
                minMonsters: minMonsters, group: group);

        private static DungeonSpawnPlan BuildPlan(DungeonPopulationLimits limits, int points = 40, int seed = 3, params (string, double)[] mods)
            => DungeonPopulationBuilder.Build(Spec(mods), Dungeon(points), Store(), Species(), LevelOf, TrashHealthOf, limits, new Random(seed));

        private static int NonBoss(DungeonSpawnPlan plan) => plan.Entries.Count(e => e.Role != DungeonRole.Boss);

        // ---- solo invariant ---------------------------------------------------------------------------------

        /// <summary>Delegates every draw to an inner Random (never to base, see CLAUDE.md) and counts calls.</summary>
        private sealed class CountingRandom : Random
        {
            private readonly Random inner;
            public int Draws;
            public CountingRandom(int seed) { inner = new Random(seed); }
            public override int Next() { Draws++; return inner.Next(); }
            public override int Next(int maxValue) { Draws++; return inner.Next(maxValue); }
            public override int Next(int minValue, int maxValue) { Draws++; return inner.Next(minValue, maxValue); }
            public override long NextInt64() { Draws++; return inner.NextInt64(); }
            public override long NextInt64(long maxValue) { Draws++; return inner.NextInt64(maxValue); }
            public override long NextInt64(long minValue, long maxValue) { Draws++; return inner.NextInt64(minValue, maxValue); }
            public override double NextDouble() { Draws++; return inner.NextDouble(); }
            public override float NextSingle() { Draws++; return inner.NextSingle(); }
            public override void NextBytes(byte[] buffer) { Draws++; inner.NextBytes(buffer); }
            public override void NextBytes(Span<byte> buffer) { Draws++; inner.NextBytes(buffer); }
            protected override double Sample() { Draws++; return inner.NextDouble(); }
        }

        private static string R(double d) => d.ToString("R", CultureInfo.InvariantCulture);

        /// <summary>
        /// Everything a solo plan decides that the spawner or a kill consumes: every entry (wcid, role, point,
        /// uplift), the boss, both health multipliers, the floors and ratios, every rating, the boss level and
        /// base, XP and luminance for all three roles, the notes, and the number of rng draws taken.
        /// FROZEN: the base hashes below were captured from this exact function on commit 81b96d771, before
        /// any Task 6 edit. Changing this function invalidates them.
        /// </summary>
        private static string Fingerprint(DungeonSpawnPlan plan, int draws)
        {
            var sb = new StringBuilder();
            sb.Append("draws=").Append(draws).Append(';');
            foreach (var e in plan.Entries)
                sb.Append(e.Wcid).Append('/').Append((int)e.Role).Append('/').Append(e.Point?.Cell ?? 0).Append('/').Append(e.UpliftLevel).Append(',');
            sb.Append(";boss=").Append(plan.BossWcid).Append(";fam=").Append(plan.FamilyId)
              .Append(";hp=").Append(R(plan.HealthMultiplier)).Append(";bossHp=").Append(R(plan.BossHealthMultiplier))
              .Append(";floor=").Append(plan.TrashHealthFloor).Append(";norm=").Append(R(plan.HealthNormalizeRatio))
              .Append(";dr=").Append(plan.DamageRating).Append(";bossDr=").Append(plan.BossDamageRating)
              .Append(";drr=").Append(plan.DamageResistRating).Append(";bossDrr=").Append(plan.BossDamageResistRating)
              .Append(";bossLevel=").Append(plan.BossLevel).Append(";poolMax=").Append(plan.PoolMaxBase)
              .Append(";bossBase=").Append(R(plan.BossHealthBase));
            foreach (var role in new[] { DungeonRole.Trash, DungeonRole.Elite, DungeonRole.Boss })
                sb.Append(";xp").Append((int)role).Append('=').Append(DungeonPopulationBuilder.XpFor(plan, 800000, role))
                  .Append(";lum").Append((int)role).Append('=').Append(DungeonPopulationBuilder.LuminanceFor(plan, 1100000, 200, role));
            sb.Append(";notes=").Append(string.Join("|", plan.Notes));
            return sb.ToString();
        }

        private static string Hash(string s)
        {
            using (var sha = SHA256.Create())
                return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(s)).Select(b => b.ToString("x2")));
        }

        private sealed class SoloScenario
        {
            public string Name;
            public DungeonGemSpec Spec;
            public int Points;
            public Func<uint, uint> Health;
            public Func<GroupScaling, DungeonPopulationLimits> Limits;
            public int Seed;
            public string BaseHash;
            public int BaseDraws;
        }

        // FROZEN BASE HASHES. They pin the solo plan to the pre-Group-Threads builder, so they are a one-time
        // proof, not a permanent contract. A later, DELIBERATE change to solo plan behaviour (a new plan field
        // in Fingerprint, a roster or modifier rule, a new rng draw) will fail Solo_plans_are_identical_to_the_
        // pre_group_base_commit. To regenerate: confirm the change is intended for solo runs, temporarily make
        // that test write "{Name}\t{Hash(fp)}\t{draws}" for each scenario to a scratch file instead of
        // asserting, run it alone, paste the new BaseHash/BaseDraws values below, restore the assertions, and
        // say in the commit that the solo baseline moved and why.
        // The LASTING equivalence check is Explicit_solo_snapshot_builds_the_same_plan_as_no_group_argument:
        // it compares no group argument against GroupScaling.Solo and a roster of one on the current code, so
        // it keeps proving "solo takes no group path" after the frozen hashes are regenerated.
        //
        // Every scenario pins modifierLevelExponent: 0 (the modifier magnitude curve, added after the base
        // commit, OFF). Spec() is level 100, below the curve's anchor (185), so the shipped exponent would move
        // every modifier-derived field in Fingerprint - the curated boss's own boss_guarded included - for a
        // reason that has nothing to do with groups. Pinning it off keeps these hashes what they claim to be, a
        // proof that the GROUP path leaves a solo plan alone; the curve has its own tests.

        /// <summary>
        /// Captured on the base commit 81b96d771 (before this task touched the builder) by building each
        /// scenario with no group argument and hashing <see cref="Fingerprint"/>. Covers a plain run, Teeming
        /// past the point count, the min-monsters floor with a trash health floor, a MaxMonsters cap below the
        /// point count, the health curve with boss normalization, the legacy boss path, and zero curated points.
        /// </summary>
        private static IEnumerable<SoloScenario> SoloScenarios()
        {
            yield return new SoloScenario { Name = "plain12", Spec = Spec(), Points = 12, Health = HealthOf, Seed = 3,
                Limits = g => new DungeonPopulationLimits(120, 2.0, 3.0, 8, 0.5, rewardScalingEnabled: false, healthCurveEnabled: false, minMonsters: 0, modifierLevelExponent: 0.0, group: g),
                BaseHash = "0e19b626111fc313557c3bdb22a797caeb63e658b0c8cb4edfdd8a71e5536184", BaseDraws = 26 };
            yield return new SoloScenario { Name = "teeming10", Spec = Spec(("teeming", 1.75)), Points = 10, Health = HealthOf, Seed = 3,
                Limits = g => new DungeonPopulationLimits(120, 2.0, 3.0, 8, 0.5, rewardScalingEnabled: false, healthCurveEnabled: false, minMonsters: 0, modifierLevelExponent: 0.0, group: g),
                BaseHash = "bb30265d303a3fd7a51bdd204f285f78f6a86964ff1ebc3362dfe259166a1a75", BaseDraws = 30 };
            yield return new SoloScenario { Name = "hardy16min40", Spec = Spec(("hardy", 1.6)), Points = 16, Health = TrashHealthOf, Seed = 17,
                Limits = g => new DungeonPopulationLimits(120, 2.0, 3.0, 8, 0.5, rewardScalingEnabled: false, trashHealthFloorRatio: 0.5, healthCurveEnabled: false, minMonsters: 40, modifierLevelExponent: 0.0, group: g),
                BaseHash = "565c5e9d23840b99d586e51a1658bc004625217dd1d402cb65366154c0990fb9", BaseDraws = 58 };
            yield return new SoloScenario { Name = "capped50", Spec = Spec(), Points = 50, Health = HealthOf, Seed = 3,
                Limits = g => new DungeonPopulationLimits(8, 2.0, 3.0, 8, 0.5, rewardScalingEnabled: false, healthCurveEnabled: false, minMonsters: 0, modifierLevelExponent: 0.0, group: g),
                BaseHash = "9f4eff7a4fc1e826e6c12d7f102c6a13150d11bf10bbc3bbf65037d54545a67a", BaseDraws = 60 };
            yield return new SoloScenario { Name = "curve40", Spec = Spec(("hardy", 1.3)), Points = 40, Health = TrashHealthOf, Seed = 5,
                Limits = g => new DungeonPopulationLimits(120, 2.0, 3.0, 8, 0.5, trashHealthFloorRatio: 0.5, minMonsters: 40, modifierLevelExponent: 0.0, group: g),
                BaseHash = "c314e0e81f150bde693a840e3cf052d65830d3ddc2f41fb7a1c0fd814cd58a8e", BaseDraws = 82 };
            yield return new SoloScenario { Name = "legacyBoss40", Spec = Spec(), Points = 40, Health = TrashHealthOf, Seed = 9,
                Limits = g => new DungeonPopulationLimits(120, 2.0, 3.0, 8, 0.5, bossNormalize: false, minMonsters: 40, modifierLevelExponent: 0.0, group: g),
                BaseHash = "25b9294347d9f36e48385cd8f27e867c99eb8d1795f2e28aeb071c99ae36463c", BaseDraws = 82 };
            yield return new SoloScenario { Name = "empty0", Spec = Spec(), Points = 0, Health = HealthOf, Seed = 3,
                Limits = g => new DungeonPopulationLimits(120, 2.0, 3.0, 8, 0.5, minMonsters: 40, modifierLevelExponent: 0.0, group: g),
                BaseHash = "ae69558f269973cc3ddfe617d0d57b83033462eb2841de622552a66a6eb39b24", BaseDraws = 2 };
        }

        private static (string Fingerprint, int Draws) BuildSolo(SoloScenario s, GroupScaling group)
        {
            var rng = new CountingRandom(s.Seed);
            var plan = DungeonPopulationBuilder.Build(s.Spec, Dungeon(s.Points), Store(), Species(), LevelOf, s.Health, s.Limits(group), rng);
            return (Fingerprint(plan, rng.Draws), rng.Draws);
        }

        [TestMethod]
        public void Solo_plans_are_identical_to_the_pre_group_base_commit()
        {
            foreach (var s in SoloScenarios())
            {
                var (fp, draws) = BuildSolo(s, null);
                Assert.AreEqual(s.BaseDraws, draws, $"{s.Name}: a solo build must take exactly the rng draws it took before groups existed");
                Assert.AreEqual(s.BaseHash, Hash(fp), $"{s.Name}: solo plan drifted from the base commit. Now: {fp}");
            }
        }

        [TestMethod]
        public void Explicit_solo_snapshot_builds_the_same_plan_as_no_group_argument()
        {
            foreach (var s in SoloScenarios())
            {
                var none = BuildSolo(s, null);
                var solo = BuildSolo(s, GroupScaling.Solo);
                var one = BuildSolo(s, Group(1));

                Assert.AreEqual(none.Fingerprint, solo.Fingerprint, $"{s.Name}: GroupScaling.Solo must be a no-op");
                Assert.AreEqual(none.Fingerprint, one.Fingerprint, $"{s.Name}: a roster of one computes to Solo and must be a no-op");
                Assert.AreEqual(s.BaseHash, Hash(solo.Fingerprint), $"{s.Name}: explicit Solo drifted from the base commit");
            }
        }

        [TestMethod]
        public void Unset_group_reads_as_solo_and_a_solo_plan_carries_neutral_group_fields()
        {
            Assert.AreSame(GroupScaling.Solo, default(DungeonPopulationLimits).Group, "a default limits struct must read as Solo, never null");
            Assert.AreSame(GroupScaling.Solo, Limits().Group);

            var plan = BuildPlan(Limits());
            Assert.AreEqual(1, plan.GroupRosterSize);
            Assert.AreEqual(1.0, plan.GroupCountTarget);
            Assert.AreEqual(1.0, plan.GroupCountActual);
            Assert.AreEqual(1.0, plan.GroupHealthMult);
            Assert.AreEqual(0, plan.GroupDamageRatingBonus);
            Assert.AreEqual(1.0, plan.GroupTrashRewardFactor);
            Assert.AreEqual(1.0, plan.GroupBossRewardFactor);
        }

        // ---- count and health -------------------------------------------------------------------------------

        [TestMethod]
        public void Two_members_on_a_40_slot_base_field_60_with_H_four_thirds_and_boss_times_two()
        {
            var solo = BuildPlan(Limits(), mods: ("hardy", 1.6));
            var duo = BuildPlan(Limits(Group(2)), mods: ("hardy", 1.6));

            Assert.AreEqual(40, NonBoss(solo), "guard: the 40-slot solo base");
            Assert.AreEqual(60, NonBoss(duo), "E 2, C 1.5: round(40 x 1.5)");
            Assert.AreEqual(1, duo.Entries.Count(e => e.Role == DungeonRole.Boss), "still exactly one boss");
            Assert.AreEqual(DungeonRole.Boss, duo.Entries.Last().Role, "I6: the boss entry stays last");

            Assert.AreEqual(2, duo.GroupRosterSize);
            Assert.AreEqual(1.5, duo.GroupCountTarget, 1e-12);
            Assert.AreEqual(1.5, duo.GroupCountActual, 1e-12);
            Assert.AreEqual(4.0 / 3.0, duo.GroupHealthMult, 1e-12);
            Assert.AreEqual(solo.HealthMultiplier * 4.0 / 3.0, duo.HealthMultiplier, 1e-12, "H multiplies the finished pack multiplier");
            Assert.AreEqual(solo.BossHealthMultiplier * 2.0, duo.BossHealthMultiplier, 1e-12, "E multiplies the finished boss multiplier");
            Assert.IsTrue(duo.BossHealthMultiplier >= duo.HealthMultiplier, "boss >= pack survives the group factors");
        }

        [TestMethod]
        public void Two_members_scale_the_count_from_the_curated_points_as_well_as_the_floor()
        {
            // No floor: the 40 comes from 40 curated points at count_mult 1, so the group count is round(40 x 1.5).
            var solo = BuildPlan(Limits(minMonsters: 0));
            var duo = BuildPlan(Limits(Group(2), minMonsters: 0));

            Assert.AreEqual(40, NonBoss(solo));
            Assert.AreEqual(60, NonBoss(duo));
            Assert.AreEqual(4.0 / 3.0, duo.GroupHealthMult, 1e-12);
        }

        [TestMethod]
        public void Ten_members_cap_at_the_group_max_of_180_and_H_is_ten_over_four_and_a_half()
        {
            var ten = BuildPlan(Limits(Group(10)));

            Assert.AreEqual(180, NonBoss(ten), "C 5.5 wants 220; the group cap of 180 binds, not MaxMonsters 120");
            Assert.AreEqual(5.5, ten.GroupCountTarget, 1e-12);
            Assert.AreEqual(4.5, ten.GroupCountActual, 1e-12);
            Assert.AreEqual(10.0 / 4.5, ten.GroupHealthMult, 1e-12);
            Assert.AreEqual(10.0 / 4.5, ten.HealthMultiplier, 1e-12, "no health modifiers on this gem, so the pack multiplier IS H");
            Assert.AreEqual(BuildPlan(Limits()).BossHealthMultiplier * 10.0, ten.BossHealthMultiplier, 1e-12);
        }

        [TestMethod]
        public void A_group_cap_below_the_solo_count_keeps_the_solo_count_and_does_not_inflate_H()
        {
            var capped = BuildPlan(Limits(Group(2, groupMax: 30)));

            Assert.AreEqual(40, NonBoss(capped), "R27: never below the solo count");
            Assert.AreEqual(1.0, capped.GroupCountActual, 1e-12);
            Assert.AreEqual(2.0, capped.GroupHealthMult, 1e-12, "H = E / 1.0, never above E");
        }

        [TestMethod]
        public void A_solo_count_capped_by_max_monsters_caps_C_actual_at_E_so_H_never_drops_below_one()
        {
            // 50 points, MaxMonsters 8, group cap 180, N = 2: the uncapped group count would be 75 (C_actual
            // 9.375, H 0.21). Capped at floor(8 x E) = 16.
            var plan = BuildPlan(Limits(Group(2, groupMax: 180), max: 8, minMonsters: 0), points: 50);

            Assert.AreEqual(8, NonBoss(BuildPlan(Limits(max: 8, minMonsters: 0), points: 50)), "guard: solo is capped at 8");
            Assert.AreEqual(16, NonBoss(plan));
            Assert.AreEqual(2.0, plan.GroupCountActual, 1e-12);
            Assert.AreEqual(1.0, plan.GroupHealthMult, 1e-12);
        }

        [TestMethod]
        public void Zero_curated_points_stay_empty_for_a_group()
        {
            var plan = BuildPlan(Limits(Group(4)), points: 0);

            Assert.AreEqual(0, NonBoss(plan));
            Assert.AreEqual(1.0, plan.GroupCountActual, 1e-12);
            Assert.AreEqual(4.0, plan.GroupHealthMult, 1e-12, "no count to share the effort with, so H = E");
        }

        // ---- damage rating (R15) ----------------------------------------------------------------------------

        [TestMethod]
        public void Boss_damage_rating_is_the_floor_or_mods_plus_the_group_bonus()
        {
            var solo = BuildPlan(Limits());
            var duo = BuildPlan(Limits(Group(2)));

            Assert.AreEqual(solo.BossDamageRating, duo.BossDamageRating, "the builder's floored boss rating is unchanged by the group");
            Assert.AreEqual(Math.Max(solo.BossDamageRating, solo.DamageRating + DungeonPopulationLimits.DefaultBossDamageRatingFloor), duo.BossDamageRating,
                "guard: that rating is max(mods, pack + floor)");
            Assert.AreEqual(solo.DamageRating, duo.DamageRating, "the bonus is never folded into the pack rating");
            Assert.AreEqual(15, duo.GroupDamageRatingBonus);

            Assert.AreEqual(duo.BossDamageRating + 15, ThreadDungeonSpawner.StampedDamageRating(duo, isBoss: true));
            Assert.AreEqual(duo.DamageRating + 15, ThreadDungeonSpawner.StampedDamageRating(duo, isBoss: false));

            Assert.AreEqual(solo.BossDamageRating, ThreadDungeonSpawner.StampedDamageRating(solo, isBoss: true), "solo stamps exactly the plan rating");
            Assert.AreEqual(solo.DamageRating, ThreadDungeonSpawner.StampedDamageRating(solo, isBoss: false));
        }

        [TestMethod]
        public void Group_damage_bonus_caps_and_the_stamp_saturates_instead_of_wrapping()
        {
            Assert.AreEqual(300, BuildPlan(Limits(Group(30))).GroupDamageRatingBonus, "15 x 29 = 435 caps at 300");

            var plan = new DungeonSpawnPlan { BossDamageRating = int.MaxValue - 5, GroupDamageRatingBonus = 100 };
            Assert.AreEqual(int.MaxValue, ThreadDungeonSpawner.StampedDamageRating(plan, isBoss: true));
        }

        // ---- XP and luminance (R16) -------------------------------------------------------------------------

        /// <summary>N = 4 on the 40-slot base: E 4, C 2.5, 100 slots, C_actual 2.5, H 1.6, B 1.15, T = 4 x share(4).</summary>
        private static (DungeonSpawnPlan Solo, DungeonSpawnPlan Quad, double H, double E, double B, double T) FourMembers()
        {
            var solo = BuildPlan(Limits());
            var quad = BuildPlan(Limits(Group(4)));

            const double e = 4.0;
            const double b = 1.15;
            var h = e / 2.5;
            var t = 4 * Fellowship.GetMemberSharePercent(4, 0.0);

            Assert.AreEqual(100, NonBoss(quad), "guard: round(40 x 2.5)");
            Assert.AreEqual(h, quad.GroupHealthMult, 1e-12, "guard: H");

            return (solo, quad, h, e, b, t);
        }

        [TestMethod]
        public void XpFor_prices_trash_and_elite_at_H_B_over_T_and_the_boss_at_E_B_over_T()
        {
            var (solo, quad, h, e, b, t) = FourMembers();

            Assert.AreEqual(h * b / t, quad.GroupTrashRewardFactor, 1e-12);
            Assert.AreEqual(e * b / t, quad.GroupBossRewardFactor, 1e-12);

            foreach (var baseXp in new long[] { 80000, 800000, 1100000 })
            {
                var trash = DungeonPopulationBuilder.XpFor(solo, baseXp, DungeonRole.Trash);
                var elite = DungeonPopulationBuilder.XpFor(solo, baseXp, DungeonRole.Elite);
                var boss = DungeonPopulationBuilder.XpFor(solo, baseXp, DungeonRole.Boss);

                Assert.AreEqual((long)Math.Round(trash * (h * b / t)), DungeonPopulationBuilder.XpFor(quad, baseXp, DungeonRole.Trash), $"trash at {baseXp}");
                Assert.AreEqual((long)Math.Round(elite * (h * b / t)), DungeonPopulationBuilder.XpFor(quad, baseXp, DungeonRole.Elite), $"elite at {baseXp}");
                Assert.AreEqual((long)Math.Round(boss * (e * b / t)), DungeonPopulationBuilder.XpFor(quad, baseXp, DungeonRole.Boss), $"boss at {baseXp}");
            }
        }

        [TestMethod]
        public void LuminanceFor_applies_the_same_factors_before_its_rounding()
        {
            var (solo, quad, h, e, b, t) = FourMembers();

            // 1,100,000 / 4,000 = 275, half-up to the 50-grid = 300 lum base; then role, scale and the modifier.
            double Unrounded(DungeonRole role) => 300 * DungeonRewardMath.RoleRate(role) * solo.LumScale * solo.LumMultiplier;

            Assert.AreEqual((int)Math.Round(Unrounded(DungeonRole.Trash) * h * b / t, MidpointRounding.AwayFromZero),
                DungeonPopulationBuilder.LuminanceFor(quad, 1100000, 200, DungeonRole.Trash));
            Assert.AreEqual((int)Math.Round(Unrounded(DungeonRole.Elite) * h * b / t, MidpointRounding.AwayFromZero),
                DungeonPopulationBuilder.LuminanceFor(quad, 1100000, 200, DungeonRole.Elite));
            Assert.AreEqual((int)Math.Round(Unrounded(DungeonRole.Boss) * e * b / t, MidpointRounding.AwayFromZero),
                DungeonPopulationBuilder.LuminanceFor(quad, 1100000, 200, DungeonRole.Boss));

            Assert.AreEqual((int)Math.Round(Unrounded(DungeonRole.Trash), MidpointRounding.AwayFromZero),
                DungeonPopulationBuilder.LuminanceFor(solo, 1100000, 200, DungeonRole.Trash), "guard: the solo value the factors apply to");
            Assert.AreEqual(0, DungeonPopulationBuilder.LuminanceFor(quad, 1100000, 184, DungeonRole.Boss), "the level-185 gate still applies");
        }

        [TestMethod]
        public void Group_xp_keeps_a_disabled_scale_at_zero_and_a_garbage_factor_reads_as_no_correction()
        {
            var (_, quad, _, _, _, _) = FourMembers();

            quad.XpScale = 0.0;
            Assert.AreEqual(0L, DungeonPopulationBuilder.XpFor(quad, 800000, DungeonRole.Trash), "scale 0 disables XP for groups too");

            var solo = BuildPlan(Limits());
            var broken = BuildPlan(Limits(Group(4)));
            broken.GroupTrashRewardFactor = double.NaN;
            broken.GroupBossRewardFactor = -1.0;

            Assert.AreEqual(DungeonPopulationBuilder.XpFor(solo, 800000, DungeonRole.Trash), DungeonPopulationBuilder.XpFor(broken, 800000, DungeonRole.Trash));
            Assert.AreEqual(DungeonPopulationBuilder.XpFor(solo, 800000, DungeonRole.Boss), DungeonPopulationBuilder.XpFor(broken, 800000, DungeonRole.Boss));

            var tiny = BuildPlan(Limits(Group(4)));
            tiny.GroupTrashRewardFactor = 1e-12;
            Assert.AreEqual(1L, DungeonPopulationBuilder.XpFor(tiny, 800000, DungeonRole.Trash), "a paying kill never prices to nothing");
        }

        [TestMethod]
        public void The_roster_size_flag_not_the_factor_value_gates_the_reward_correction()
        {
            var untouched = BuildPlan(Limits());
            var tampered = BuildPlan(Limits());
            tampered.GroupTrashRewardFactor = 2.0;
            tampered.GroupBossRewardFactor = 2.0;

            Assert.AreEqual(1, tampered.GroupRosterSize, "guard: still a solo plan");

            foreach (var role in new[] { DungeonRole.Trash, DungeonRole.Elite, DungeonRole.Boss })
            {
                Assert.AreEqual(DungeonPopulationBuilder.XpFor(untouched, 800000, role), DungeonPopulationBuilder.XpFor(tampered, 800000, role), $"xp {role}");
                Assert.AreEqual(DungeonPopulationBuilder.LuminanceFor(untouched, 1100000, 200, role), DungeonPopulationBuilder.LuminanceFor(tampered, 1100000, 200, role), $"lum {role}");
            }
        }

        // ---- legacy boss health floor (boss normalization off) -----------------------------------------------

        [TestMethod]
        public void Legacy_boss_floor_ratio_is_untouched_for_solo_and_scaled_by_E_over_H_for_a_group()
        {
            var solo = BuildPlan(Limits());
            Assert.AreEqual(solo.BossHealthFloorRatio, ThreadDungeonSpawner.LegacyBossFloorRatio(solo, GroupScaling.Solo.Effort), "solo: the plan ratio as is");
            Assert.AreEqual(solo.BossHealthFloorRatio, ThreadDungeonSpawner.LegacyBossFloorRatio(solo, 7.0), "solo ignores any effort passed in");

            var group = Group(2);
            var duo = BuildPlan(Limits(group));
            Assert.AreEqual(4.0 / 3.0, duo.GroupHealthMult, 1e-12, "guard: H");

            var scaled = ThreadDungeonSpawner.LegacyBossFloorRatio(duo, group.Effort);
            Assert.AreEqual(duo.BossHealthFloorRatio * 2.0 / (4.0 / 3.0), scaled, 1e-12, "ratio x E / H");

            // The floor term through the real target arithmetic: an observed pack max carrying H maps back to
            // (pack / H) x E x ratio, so the boss floor scales by E.
            const uint packAtH = 12000; // a creature whose solo max was 9000, times H 4/3
            var target = DungeonRewardMath.BossHealthTarget(1, 1.0, packAtH, scaled);
            Assert.AreEqual((int)Math.Round(9000 * 2.0 * duo.BossHealthFloorRatio), target);

            duo.GroupHealthMult = double.NaN;
            Assert.AreEqual(duo.BossHealthFloorRatio, ThreadDungeonSpawner.LegacyBossFloorRatio(duo, group.Effort), "garbage H reads as no scaling");
        }

        [TestMethod]
        public void Legacy_boss_target_call_site_uses_the_group_aware_floor_ratio()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadDungeonSpawner.cs");
            var body = PooledLootSourceText.MethodBody(src, "private static PlaceOutcome TryPlaceOnce(ThreadDungeonRun run, Landblock landblock, DungeonSpawnPlan plan, DungeonSpawnPlanEntry entry,");

            StringAssert.Contains(body, "DungeonRewardMath.BossHealthTarget(baseMax, hpMult, observedMaxNonBoss, LegacyBossFloorRatio(plan, run.Group.Effort));");
        }

        [TestMethod]
        public void The_group_log_suffix_is_appended_only_for_group_runs()
        {
            var body = TryPopulateBody();
            Assert.IsTrue(System.Text.RegularExpressions.Regex.IsMatch(body, @"\(run\.IsGroup\s*\?\s*\$"" group n=\{plan\.GroupRosterSize\}"),
                "the group suffix must be conditional on run.IsGroup");
            Assert.AreEqual(1, CountOf(body, "group n="), "one suffix, the conditional one");
        }

        // ---- spawner wiring (source-text pins) --------------------------------------------------------------

        private static string TryPopulateBody()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadDungeonSpawner.cs");
            return PooledLootSourceText.MethodBody(src, "public static void TryPopulate(ThreadDungeonRun run, Landblock landblock)");
        }

        [TestMethod]
        public void Group_pooled_stamp_immediately_precedes_the_pinned_switch_stamp()
        {
            var body = TryPopulateBody();

            const string groupStamp = "if (run.IsGroup) run.MarkPooledLoot(true);";
            const string pinned = "run.MarkPooledLoot(PropertyManager.GetBool(\"dynamic_dungeons_pooled_loot_enabled\", true).Item);";

            var group = body.IndexOf(groupStamp, StringComparison.Ordinal);
            var stamp = body.IndexOf(pinned, StringComparison.Ordinal);

            Assert.IsTrue(group >= 0, "TryPopulate must force pooled loot for a group run (R13)");
            Assert.IsTrue(stamp > group, "the group stamp must run before the write-once switch stamp");
            Assert.AreEqual(string.Empty, body.Substring(group + groupStamp.Length, stamp - group - groupStamp.Length).Trim(),
                "the group stamp is the line immediately before the pinned stamp");
            Assert.AreEqual(2, CountOf(body, "run.MarkPooledLoot("), "exactly the two stamps");
        }

        [TestMethod]
        public void Populate_passes_the_run_snapshot_and_resolves_the_group_before_any_placement()
        {
            var body = TryPopulateBody();

            // "group: run.Group," since 2026-10-08: the run ceiling raise appended four named arguments after it
            // (healthCurveTopLevel, reachUp, statCurve, defenseCurveRateAbove375), so it is no longer the last.
            var limitsArg = body.IndexOf("group: run.Group,", StringComparison.Ordinal);
            var build = body.IndexOf("plan = DungeonPopulationBuilder.Build(", StringComparison.Ordinal);
            var resolve = body.IndexOf("run.MarkGroupResolved(plan.GroupCountActual, plan.GroupHealthMult);", StringComparison.Ordinal);
            var queue = body.IndexOf("var queue = new Queue<DungeonSpawnPlanEntry>(plan.Entries);", StringComparison.Ordinal);

            Assert.IsTrue(limitsArg >= 0 && limitsArg < build, "the limits must carry run.Group, read from the lock-time snapshot");
            Assert.IsTrue(resolve > build, "MarkGroupResolved runs after the plan is built");
            Assert.IsTrue(queue > resolve, "and before the placement queue exists");
            Assert.AreEqual(1, CountOf(body, "run.MarkGroupResolved("), "resolved once");
            Assert.IsTrue(System.Text.RegularExpressions.Regex.IsMatch(body,
                    @"if \(run\.IsGroup\)\s*run\.MarkGroupResolved\(plan\.GroupCountActual, plan\.GroupHealthMult\);"),
                "a solo run never stamps the group resolution");
        }

        [TestMethod]
        public void TryPlace_stamps_damage_rating_through_the_group_aware_helper()
        {
            // The rating stamp lives in TryPlaceOnce, which TryPlace calls once per candidate point.
            var src = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadDungeonSpawner.cs");
            var body = PooledLootSourceText.MethodBody(src, "private static PlaceOutcome TryPlaceOnce(ThreadDungeonRun run, Landblock landblock, DungeonSpawnPlan plan, DungeonSpawnPlanEntry entry,");

            StringAssert.Contains(body, "var dr = StampedDamageRating(plan, isBoss);");
            Assert.IsFalse(body.Contains("var dr = isBoss ? plan.BossDamageRating : plan.DamageRating;"), "the pre-group stamp would drop the bonus");
        }

        private static int CountOf(string haystack, string needle)
        {
            var count = 0;
            for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
                count++;
            return count;
        }
    }
}
