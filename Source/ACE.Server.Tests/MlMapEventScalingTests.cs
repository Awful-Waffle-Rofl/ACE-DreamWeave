using System;
using System.Collections.Generic;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.MlDigsite;
using ACE.Server.Tests.ThreadDungeons;
using ACE.Server.WorldEvents;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Position = ACE.Entity.Position;

namespace ACE.Server.Tests
{
    /// <summary>
    /// RoZ round 19 level-spread scaling for Marae Lassel map events (MlMapEventScaling). Every pure function
    /// is asserted directly; ApplySpreadScaling is driven against a real in-memory Creature (TestGameTables
    /// supplies the retail attribute formulas) with settings passed as a value. The ONLY PropertyManager keys
    /// read are the two kill switches MlDigsiteBossDamage.TryNoteHit consults, and every test that reaches that
    /// read seeds both and restores them to their registered defaults in a finally. The engine contest the floor inverts is checked
    /// against SkillCheck.GetSkillChance itself, never against a re-typed copy of the formula.
    /// </summary>
    [TestClass]
    public class MlMapEventScalingTests
    {
        private static uint nextGuid = 0x7EF00000;
        private static uint nextWcid = 996000;

        static MlMapEventScalingTests()
        {
            TestGameTables.EnsureInitialized();
        }

        // ---- the contest ---------------------------------------------------------------------------------

        [TestMethod]
        public void DefenseFloor_solves_the_engine_contest_to_the_minimum_chance()
        {
            var target = MlMapEventScaling.DefenseFloor(1000, 300, 0.60);

            Assert.IsTrue(target < 1000);
            Assert.IsTrue(SkillCheck.GetSkillChance(300, (int)target) >= 0.60, "the keyed attack reaches p at the floor");
            Assert.IsTrue(SkillCheck.GetSkillChance(300, (int)target + 1) < 0.60, "and the floor is the HIGHEST defense that does");
        }

        [TestMethod]
        public void DefenseFloor_never_raises_a_defense()
        {
            Assert.AreEqual(100u, MlMapEventScaling.DefenseFloor(100, 900, 0.60), "a strong attacker leaves an easy defense alone");
            Assert.AreEqual(0u, MlMapEventScaling.DefenseFloor(0, 50, 0.99));

            for (uint def = 0; def <= 800; def += 37)
            {
                for (uint atk = 1; atk <= 800; atk += 41)
                    Assert.IsTrue(MlMapEventScaling.DefenseFloor(def, atk, 0.7) <= def, $"def {def} atk {atk}");
            }
        }

        [TestMethod]
        public void DefenseFloor_ignores_an_untrained_attack_family()
        {
            Assert.AreEqual(500u, MlMapEventScaling.DefenseFloor(500, 0, 0.60));
        }

        [TestMethod]
        public void DefenseFloor_stops_at_zero()
        {
            Assert.AreEqual(0u, MlMapEventScaling.DefenseFloor(500, 5, 0.99));
        }

        [TestMethod]
        public void TrashAttackCap_caps_to_the_max_chance_and_never_raises()
        {
            var capped = MlMapEventScaling.TrashAttackCap(900, 200, 0.75);

            Assert.IsTrue(capped < 900);
            Assert.IsTrue(SkillCheck.GetSkillChance((int)capped, 200) <= 0.75);
            Assert.IsTrue(SkillCheck.GetSkillChance((int)capped + 1, 200) > 0.75, "the cap is the highest attack that stays at or under p");

            Assert.AreEqual(150u, MlMapEventScaling.TrashAttackCap(150, 200, 0.75), "a weak attacker is left alone");
        }

        // ---- InitLevel conversion ------------------------------------------------------------------------

        [TestMethod]
        public void InitLevelFor_moves_InitLevel_one_for_one_with_the_skill()
        {
            var r = MlMapEventScaling.InitLevelFor(initLevel: 300, skill: 400, mult: 1.0, imbues: 0, targetEffective: 250);

            Assert.IsTrue(r.Changed);
            Assert.IsFalse(r.Clamped);
            Assert.AreEqual(150u, r.NewInitLevel);
        }

        [TestMethod]
        public void InitLevelFor_solves_on_the_effective_skill_with_weapon_mod_and_imbues()
        {
            var r = MlMapEventScaling.InitLevelFor(initLevel: 300, skill: 400, mult: 1.2, imbues: 10, targetEffective: 300);

            var newSkill = 400 - (300 - r.NewInitLevel);
            var effective = Math.Round(newSkill * 1.2 + 10, MidpointRounding.AwayFromZero);

            Assert.IsTrue(effective <= 300, $"effective {effective} must not land above the target");
            Assert.IsTrue(Math.Round((newSkill + 1) * 1.2 + 10, MidpointRounding.AwayFromZero) > 300, "and it is the highest skill that does");
        }

        [TestMethod]
        public void InitLevelFor_clamps_at_zero_and_reports_it()
        {
            var r = MlMapEventScaling.InitLevelFor(initLevel: 50, skill: 300, mult: 1.0, imbues: 0, targetEffective: 100);

            Assert.AreEqual(0u, r.NewInitLevel);
            Assert.IsTrue(r.Clamped);
            Assert.IsTrue(r.Changed);
        }

        [TestMethod]
        public void InitLevelFor_never_raises()
        {
            var r = MlMapEventScaling.InitLevelFor(initLevel: 100, skill: 200, mult: 1.0, imbues: 0, targetEffective: 900);

            Assert.IsFalse(r.Changed);
            Assert.AreEqual(100u, r.NewInitLevel);
        }

        // ---- trash damage, health, XP --------------------------------------------------------------------

        [TestMethod]
        public void TrashDamageRating_scales_by_health_ratio_through_the_engine_rating_curve()
        {
            // 250/500 = 0.5x -> the engine's negative-rating curve (100 / (100 + |r|)) needs -100
            Assert.AreEqual(-100, MlMapEventScaling.TrashDamageRating(0, 250, 500, 0.25));
            Assert.AreEqual(0.5, WorldEventBossDamageController.RatingToMult(-100), 1e-9);
        }

        [TestMethod]
        public void TrashDamageRating_is_floored_and_never_raises()
        {
            // 50/500 = 0.1x is below the 0.25 floor -> 0.25x -> -300
            Assert.AreEqual(-300, MlMapEventScaling.TrashDamageRating(0, 50, 500, 0.25));

            Assert.AreEqual(40, MlMapEventScaling.TrashDamageRating(40, 5000, 500, 0.25), "a tough player leaves the rating alone");
            Assert.AreEqual(40, MlMapEventScaling.TrashDamageRating(40, 0, 500, 0.25), "an unreadable health is no change");

            // composes with an existing positive rating: 1.4x * 0.5 = 0.7x
            var composed = MlMapEventScaling.TrashDamageRating(40, 250, 500, 0.25);
            Assert.AreEqual(0.7, WorldEventBossDamageController.RatingToMult(composed), 0.01);
        }

        [TestMethod]
        public void XpScale_is_the_squared_level_ratio_and_never_raises()
        {
            Assert.AreEqual(0.16, MlMapEventScaling.XpScale(100, 250, 2.0), 1e-9);
            Assert.AreEqual(1.0, MlMapEventScaling.XpScale(275, 250, 2.0), 1e-9);
            Assert.AreEqual(1.0, MlMapEventScaling.XpScale(100, 0, 2.0), 1e-9, "a level-less creature is left alone");
            Assert.AreEqual(1.0, MlMapEventScaling.XpScale(100, 250, 0.0), 1e-9, "exponent 0 turns it off");

            Assert.AreEqual(128000L, MlMapEventScaling.ScaleAward(800000, 0.16));
            Assert.AreEqual(800000L, MlMapEventScaling.ScaleAward(800000, 1.0));
        }

        [TestMethod]
        public void TrashHealthScale_is_the_level_ratio_at_the_exponent()
        {
            Assert.AreEqual(0.4, MlMapEventScaling.TrashHealthScale(100, 250, 1.0), 1e-9);
            Assert.AreEqual(1.0, MlMapEventScaling.TrashHealthScale(260, 250, 1.0), 1e-9);
        }

        // ---- keying --------------------------------------------------------------------------------------

        private static readonly MlMapEventProfile Mage = new MlMapEventProfile(150, 300, 0, 0, 350, 200, 180);
        private static readonly MlMapEventProfile LowMelee = new MlMapEventProfile(100, 250, 300, 0, 0, 250, 200);
        private static readonly MlMapEventProfile HighMelee = new MlMapEventProfile(275, 900, 600, 550, 0, 500, 480);

        [TestMethod]
        public void Weakest_takes_each_attack_family_from_the_players_who_train_it()
        {
            var w = MlMapEventScaling.Weakest(new[] { Mage, LowMelee, HighMelee });

            Assert.AreEqual(100, w.Level);
            Assert.AreEqual(250u, w.MaxHealth);
            Assert.AreEqual(300u, w.MeleeAttack, "the mage does not melee, so it is not the weakest melee attacker");
            Assert.AreEqual(550u, w.MissileAttack);
            Assert.AreEqual(350u, w.MagicAttack);
            Assert.AreEqual(200u, w.MeleeDefense);
            Assert.AreEqual(180u, w.MissileDefense);
        }

        [TestMethod]
        public void Objectives_key_to_the_weakest_and_trash_to_one_sampled_player()
        {
            var profiles = new[] { HighMelee, LowMelee, Mage };

            Assert.AreEqual(100, MlMapEventScaling.KeyFor(MlMapEventRoleClass.Objective, profiles, new Random(1)).Value.Level);
            Assert.AreEqual(100, MlMapEventScaling.KeyFor(MlMapEventRoleClass.Boss, profiles, new Random(1)).Value.Level);

            var seen = new HashSet<int>();

            for (var seed = 0; seed < 64; seed++)
                seen.Add(MlMapEventScaling.KeyFor(MlMapEventRoleClass.Trash, profiles, new Random(seed)).Value.Level);

            CollectionAssert.AreEquivalent(new[] { 100, 150, 275 }, new List<int>(seen), "trash draws one real player, not a composite");
        }

        [TestMethod]
        public void KeyFor_draws_nothing_for_a_solo_player_and_returns_null_for_nobody()
        {
            var rng = new CountingRandom(7);

            Assert.AreEqual(275, MlMapEventScaling.KeyFor(MlMapEventRoleClass.Trash, new[] { HighMelee }, rng).Value.Level);
            Assert.AreEqual(0, rng.Draws);

            Assert.IsNull(MlMapEventScaling.KeyFor(MlMapEventRoleClass.Trash, new MlMapEventProfile[0], rng));
            Assert.IsNull(MlMapEventScaling.KeyFor(MlMapEventRoleClass.Objective, null, rng));
        }

        [TestMethod]
        public void Role_classes_follow_the_world_event_split()
        {
            Assert.AreEqual(MlMapEventRoleClass.Trash, MlMapEventScaling.RoleClassFor(MlDigsiteRole.Wave));
            Assert.AreEqual(MlMapEventRoleClass.Trash, MlMapEventScaling.RoleClassFor(MlDigsiteRole.Add));
            Assert.AreEqual(MlMapEventRoleClass.Boss, MlMapEventScaling.RoleClassFor(MlDigsiteRole.Boss));
            Assert.AreEqual(MlMapEventRoleClass.Objective, MlMapEventScaling.RoleClassFor(MlDigsiteRole.MiniBoss));
            Assert.AreEqual(MlMapEventRoleClass.Objective, MlMapEventScaling.RoleClassFor(MlDigsiteRole.Checkpoint));
            Assert.AreEqual(MlMapEventRoleClass.Objective, MlMapEventScaling.RoleClassFor(MlDigsiteRole.Priority));
        }

        // ---- boss health ---------------------------------------------------------------------------------

        [TestMethod]
        public void Boss_health_is_the_world_event_power_sum_curve_and_never_below_authored()
        {
            Assert.AreEqual(1.15, MlMapEventScaling.BossHealthMult(0.15, 3.0, MlMapEventScaling.PowerSum(new[] { HighMelee })), 1e-9);
            Assert.AreEqual(1.0, MlMapEventScaling.BossHealthMult(0.15, 3.0, 0.0), 1e-9);
            Assert.AreEqual(3.0, MlMapEventScaling.BossHealthMult(0.15, 3.0, 100.0), 1e-9);

            // base 0 = keep the authored health as the floor: a multiplier >= 1 on the authored pair
            var sv = MlMapEventScaling.BossHealthStartingValue(34900, 35000, 0, 1.15, out var baseSv, out var baseMax);
            Assert.AreEqual(34900u, baseSv);
            Assert.AreEqual(35000u, baseMax);
            Assert.AreEqual(34900u + 5250u, sv);
        }

        [TestMethod]
        public void Boss_base_health_rebases_below_authored_before_the_multiplier()
        {
            var sv = MlMapEventScaling.BossHealthStartingValue(34900, 35000, 1000, 1.15, out var baseSv, out var baseMax);

            Assert.AreEqual(900u, baseSv, "the rebase path can go below the authored health; ScaledStartingValue cannot");
            Assert.AreEqual(1000u, baseMax);
            Assert.AreEqual(900u + 150u, sv);
        }

        // ---- ApplySpreadScaling on a real creature -------------------------------------------------------

        [TestMethod]
        public void All_high_group_leaves_every_creature_exactly_as_authored()
        {
            var god = new MlMapEventProfile(275, 5000, 900, 900, 900, 900, 900);

            foreach (var roleClass in new[] { MlMapEventRoleClass.Trash, MlMapEventRoleClass.Objective })
            {
                var c = BuildCreature();
                var before = Snapshot(c);

                var r = MlMapEventScaling.ApplySpreadScaling(c, roleClass, new[] { god, god }, new Random(3), MlMapEventSettings.Defaults, "test");

                Assert.IsTrue(r.Applied);
                Assert.AreEqual(before, Snapshot(c), $"{roleClass}: an all-high group must get today's fight unchanged");
            }
        }

        [TestMethod]
        public void A_low_player_gets_trash_that_it_can_hit_survive_and_not_farm()
        {
            var c = BuildCreature();
            var hpBefore = c.Health.MaxValue;

            MlMapEventScaling.ApplySpreadScaling(c, MlMapEventRoleClass.Trash, new[] { LowMelee }, new Random(1), MlMapEventSettings.Defaults, "test");

            var meleeDef = EffectiveDefense(c, Skill.MeleeDefense);
            Assert.IsTrue(SkillCheck.GetSkillChance(300, (int)meleeDef) >= 0.60, $"melee defense {meleeDef}");

            Assert.AreEqual(400u + AttrPart(c, Skill.MissileDefense), c.GetCreatureSkill(Skill.MissileDefense).Base,
                "the player trains no missile weapon, so missile defense is untouched");

            var attack = c.GetCreatureSkill(Skill.HeavyWeapons).Base;
            Assert.IsTrue(SkillCheck.GetSkillChance((int)attack, 250) <= 0.75, $"trash attack {attack} vs defense 250");

            Assert.AreEqual(-100, c.DamageRating);
            Assert.IsTrue(c.Health.MaxValue < hpBefore * 0.6 && c.Health.MaxValue > hpBefore * 0.4, $"health {hpBefore}->{c.Health.MaxValue}");
            Assert.AreEqual(c.Health.MaxValue, c.Health.Current, "health is refilled to the new max");
            Assert.AreEqual(200000, c.XpOverride, "(100/200)^2 of 800000");
            Assert.AreEqual(250, c.LuminanceAward);
        }

        [TestMethod]
        public void Objective_defenses_key_to_the_weakest_but_its_offence_health_and_xp_are_untouched()
        {
            var c = BuildCreature();
            var before = Snapshot(c);

            MlMapEventScaling.ApplySpreadScaling(c, MlMapEventRoleClass.Objective, new[] { HighMelee, Mage, LowMelee }, new Random(1), MlMapEventSettings.Defaults, "test");

            Assert.IsTrue(SkillCheck.GetSkillChance(300, (int)EffectiveDefense(c, Skill.MeleeDefense)) >= 0.60, "weakest melee (300)");
            Assert.IsTrue(SkillCheck.GetSkillChance(350, (int)EffectiveDefense(c, Skill.MagicDefense)) >= 0.70, "weakest magic (350)");
            Assert.IsTrue(SkillCheck.GetSkillChance(550, (int)EffectiveDefense(c, Skill.MissileDefense)) >= 0.60, "weakest missile (550)");

            Assert.AreEqual(before.Attack, c.GetCreatureSkill(Skill.HeavyWeapons).Base);
            Assert.AreEqual(before.MaxHealth, c.Health.MaxValue);
            Assert.AreEqual(before.Xp, c.XpOverride);
            Assert.IsNull(c.DamageRating);
        }

        [TestMethod]
        public void The_floor_clamps_at_InitLevel_zero_and_reports_it()
        {
            // high attributes, low InitLevel: the attribute part alone is above the target
            var c = BuildCreature(skillInit: 20, quickness: 400, coordination: 400);

            var r = MlMapEventScaling.ApplySpreadScaling(c, MlMapEventRoleClass.Objective,
                new[] { new MlMapEventProfile(50, 100, 60, 0, 0, 50, 50) }, new Random(1), MlMapEventSettings.Defaults, "test");

            Assert.AreEqual(0u, c.GetCreatureSkill(Skill.MeleeDefense).InitLevel);
            CollectionAssert.Contains(r.ClampedDefenses, "melee");
        }

        [TestMethod]
        public void Boss_takes_power_sum_health_and_nobody_or_the_kill_switch_changes_nothing()
        {
            var c = BuildCreature();
            var max = c.Health.MaxValue;

            var r = MlMapEventScaling.ApplySpreadScaling(c, MlMapEventRoleClass.Boss, new[] { HighMelee }, new Random(1), MlMapEventSettings.Defaults, "test");

            Assert.IsTrue(r.BossHealthApplied);
            Assert.AreEqual(1.15, r.BossHealthMult, 1e-9);
            Assert.AreEqual((uint)Math.Round(max * 1.15, MidpointRounding.AwayFromZero), c.Health.MaxValue);
            Assert.AreEqual(max, r.BossBaseMax);

            var nobody = BuildCreature();
            var nobodyBefore = Snapshot(nobody);
            Assert.IsFalse(MlMapEventScaling.ApplySpreadScaling(nobody, MlMapEventRoleClass.Boss, new MlMapEventProfile[0], new Random(1), MlMapEventSettings.Defaults, "test").Applied);
            Assert.AreEqual(nobodyBefore, Snapshot(nobody));

            var off = BuildCreature();
            var offBefore = Snapshot(off);
            var disabled = new MlMapEventSettings(false, 0.6, 0.7, 0.75, 500, 0.25, 1, 2, 0.15, 3, 0, true, 3);
            Assert.IsFalse(MlMapEventScaling.ApplySpreadScaling(off, MlMapEventRoleClass.Trash, new[] { LowMelee }, new Random(1), disabled, "test").Applied);
            Assert.AreEqual(offBefore, Snapshot(off));
        }

        [TestMethod]
        public void The_in_combat_weapon_mods_are_one_for_an_unarmed_creature()
        {
            var c = BuildCreature();

            Assert.AreEqual(1.0, MlMapEventScaling.CombatMeleeDefenseMod(c), 1e-9);
            Assert.AreEqual(1.0, MlMapEventScaling.CombatOffenseMod(c), 1e-9);
        }

        // ---- the digsite boss damage branch --------------------------------------------------------------

        [TestMethod]
        public void A_creature_with_no_digsite_encounter_takes_only_the_cheap_rejection()
        {
            var c = BuildCreature();

            Assert.AreEqual(MlDigsiteBossHitOutcome.NotDigsite, MlDigsiteBossDamage.TryNoteHit(c, 500, 100, false));
            Assert.AreEqual(MlDigsiteBossHitOutcome.NotDigsite, MlDigsiteBossDamage.TryNoteHit(null, 500, 100, false));
        }

        [TestMethod]
        public void Only_the_Boss_Rush_objective_feeds_the_controller()
        {
            var waves = NewEncounter(MlDigsiteType.WavesAndMiniBoss);
            var wave = BuildCreature();
            wave.P_DigsiteEncounter = waves;
            Assert.IsNull(waves.BossDamage, "only a Boss Rush carries a controller");
            Assert.AreEqual(MlDigsiteBossHitOutcome.NotBossRush, MlDigsiteBossDamage.TryNoteHit(wave, 500, 100, false));

            var rush = NewEncounter(MlDigsiteType.BossRush);
            var boss = BuildCreature();
            var add = BuildCreature();
            boss.P_DigsiteEncounter = rush;
            add.P_DigsiteEncounter = rush;
            rush.TrackObjective(boss);

            Assert.AreEqual(MlDigsiteBossHitOutcome.NotObjective, MlDigsiteBossDamage.TryNoteHit(add, 500, 100, false));
            Assert.AreEqual(0, rush.BossDamage.PendingHits);

            try
            {
                SeedSwitches(spread: true, bossDamage: true);

                Assert.AreEqual(MlDigsiteBossHitOutcome.Noted, MlDigsiteBossDamage.TryNoteHit(boss, 500, 100, false));
                Assert.AreEqual(1, rush.BossDamage.PendingHits);
            }
            finally
            {
                SeedSwitches(spread: true, bossDamage: true);
            }
        }

        [TestMethod]
        public void Either_kill_switch_off_stops_the_controller_buffering_hits()
        {
            var rush = NewEncounter(MlDigsiteType.BossRush);
            var boss = BuildCreature();
            boss.P_DigsiteEncounter = rush;
            rush.TrackObjective(boss);

            try
            {
                SeedSwitches(spread: true, bossDamage: false);
                Assert.AreEqual(MlDigsiteBossHitOutcome.Disabled, MlDigsiteBossDamage.TryNoteHit(boss, 500, 100, false));

                SeedSwitches(spread: false, bossDamage: true);
                Assert.AreEqual(MlDigsiteBossHitOutcome.Disabled, MlDigsiteBossDamage.TryNoteHit(boss, 500, 100, false));

                Assert.AreEqual(0, rush.BossDamage.PendingHits, "a switched-off controller buffers nothing");
                Assert.AreEqual(0, rush.BossDamage.TotalHits);
            }
            finally
            {
                SeedSwitches(spread: true, bossDamage: true);
            }
        }

        private static void SeedSwitches(bool spread, bool bossDamage)
        {
            ACE.Server.Managers.PropertyManager.ModifyBool("ml_mapevent_spread_scaling_enabled", spread);
            ACE.Server.Managers.PropertyManager.ModifyBool("ml_mapevent_boss_damage_scaling_enabled", bossDamage);
        }

        // ---- the reap re-check ---------------------------------------------------------------------------

        [TestMethod]
        public void RaiseBossHealthMult_never_lowers_across_candidate_sequences()
        {
            var sequences = new[]
            {
                new[] { 1.2, 1.1, 1.3, 1.0, 0.5, 1.25, 2.0, 1.9 },
                new[] { 3.0, 1.0, 2.9, 0.0, -1.0 },
                new[] { double.NaN, 1.4, double.PositiveInfinity, 1.39, double.NegativeInfinity, 1.41 },
            };

            foreach (var seq in sequences)
            {
                var enc = NewEncounter(MlDigsiteType.BossRush);
                enc.NoteBossHealthBase(1000, 1100, 1.05);

                var high = 1.05;

                foreach (var candidate in seq)
                {
                    var moved = enc.RaiseBossHealthMult(candidate);

                    Assert.IsTrue(enc.TryGetBossHealthBase(out _, out _, out var mult));
                    Assert.IsTrue(mult >= high, $"candidate {candidate} lowered the multiplier {high} -> {mult}");
                    Assert.AreEqual(moved, mult > high, $"candidate {candidate}: the return value must report a move exactly when one happened");

                    high = mult;
                }
            }
        }

        [TestMethod]
        public void TryGetBossHealthBase_round_trips_and_reports_unset()
        {
            var enc = NewEncounter(MlDigsiteType.BossRush);

            Assert.IsFalse(enc.TryGetBossHealthBase(out _, out _, out var unsetMult), "nothing recorded yet");
            Assert.AreEqual(1.0, unsetMult, 1e-9);

            enc.NoteBossHealthBase(34900, 35000, 1.15);

            Assert.IsTrue(enc.TryGetBossHealthBase(out var sv, out var max, out var mult));
            Assert.AreEqual(34900u, sv);
            Assert.AreEqual(35000u, max);
            Assert.AreEqual(1.15, mult, 1e-9);
        }

        [TestMethod]
        public void Recheck_raises_boss_health_only_when_the_scaled_value_exceeds_the_current_one()
        {
            var c = BuildCreature();
            var sv = c.Health.StartingValue;
            var max = c.Health.MaxValue;
            var god = new MlMapEventProfile(275, 5000, 900, 900, 900, 900, 900);

            // A higher multiplier raises StartingValue, MaxValue and Current together.
            var summary = MlDigsiteManager.ApplyStandingRecheck(c, god, MlMapEventSettings.Defaults, sv, max, 1.5);
            var raisedMax = c.Health.MaxValue;
            Assert.AreEqual((uint)Math.Round(max * 1.5, MidpointRounding.AwayFromZero), raisedMax);
            Assert.AreEqual(raisedMax, c.Health.Current);
            StringAssert.Contains(summary, "hp=");

            // A LOWER multiplier from the same base must not shrink it.
            MlDigsiteManager.ApplyStandingRecheck(c, god, MlMapEventSettings.Defaults, sv, max, 1.2);
            Assert.AreEqual(raisedMax, c.Health.MaxValue, "the ratchet never lowers boss health");

            // The same multiplier again (equal, not greater) is no write at all.
            c.Health.Current = 10;
            var equalSummary = MlDigsiteManager.ApplyStandingRecheck(c, god, MlMapEventSettings.Defaults, sv, max, 1.5);
            Assert.AreEqual(10u, c.Health.Current, "an equal scaled value writes nothing, not even a refill");
            Assert.IsFalse(equalSummary.Contains("hp="), "an equal scaled value takes no health write at all");
        }

        [TestMethod]
        public void Recheck_of_an_objective_only_lowers_defenses_and_never_touches_health()
        {
            var c = BuildCreature();
            var before = Snapshot(c);

            // bossMult 0 = the objective/checkpoint branch; a strong key leaves everything alone
            var god = new MlMapEventProfile(275, 5000, 900, 900, 900, 900, 900);
            Assert.AreEqual("", MlDigsiteManager.ApplyStandingRecheck(c, god, MlMapEventSettings.Defaults, 0, 0, 0));
            Assert.AreEqual(before, Snapshot(c), "a strong newcomer never raises anything");

            // a weaker newcomer lowers the defenses they attack with, and only those
            MlDigsiteManager.ApplyStandingRecheck(c, LowMelee, MlMapEventSettings.Defaults, 0, 0, 0);

            Assert.IsTrue(c.GetCreatureSkill(Skill.MeleeDefense).Base < before.MeleeDef);
            Assert.AreEqual(before.MissileDef, c.GetCreatureSkill(Skill.MissileDefense).Base);
            Assert.AreEqual(before.MagicDef, c.GetCreatureSkill(Skill.MagicDefense).Base);
            Assert.AreEqual(before.MaxHealth, c.Health.MaxValue, "the objective branch never writes health");
            Assert.AreEqual(before.Attack, c.GetCreatureSkill(Skill.HeavyWeapons).Base);

            // and a STRONGER player arriving afterwards does not give the defense back
            var lowered = c.GetCreatureSkill(Skill.MeleeDefense).Base;
            MlDigsiteManager.ApplyStandingRecheck(c, god, MlMapEventSettings.Defaults, 0, 0, 0);
            Assert.AreEqual(lowered, c.GetCreatureSkill(Skill.MeleeDefense).Base, "never raised back");
        }

        [TestMethod]
        public void The_queued_recheck_action_runs_ApplyStandingRecheck_and_the_objective_is_queued_with_no_boss_multiplier()
        {
            var manager = PooledLootSourceText.Read("Source/ACE.Server/MlDigsite/MlDigsiteManager.cs");
            var enqueue = PooledLootSourceText.MethodBody(manager,
                "private static void EnqueueStandingRecheck(MlDigsiteEncounter encounter, Creature target, MlMapEventProfile weakest,");
            var recheck = PooledLootSourceText.MethodBody(manager,
                "private static void RecheckStandingObjectives(MlDigsiteEncounter encounter)");

            StringAssert.Contains(enqueue, "landblock.EnqueueAction(");
            StringAssert.Contains(enqueue, "ApplyStandingRecheck(target, weakest, settings, bossBaseSv, bossBaseMax, bossMult)");
            Assert.IsTrue(enqueue.IndexOf("try", StringComparison.Ordinal) > enqueue.IndexOf("EnqueueAction(", StringComparison.Ordinal),
                "the try/catch sits INSIDE the queued action");

            StringAssert.Contains(recheck, "EnqueueStandingRecheck(encounter, objective, weakest, settings, 0, 0, 0);");
            StringAssert.Contains(recheck, "EnqueueStandingRecheck(encounter, checkpoint, weakest, settings, 0, 0, 0);");
            StringAssert.Contains(recheck, "encounter.RaiseBossHealthMult(candidate)");
        }

        [TestMethod]
        public void The_hook_tries_the_digsite_branch_only_after_the_world_event_check_and_behind_one_null_check()
        {
            var body = PooledLootSourceText.MethodBody(
                PooledLootSourceText.Read("Source/ACE.Server/WorldEvents/WorldEventBossDamageHook.cs"),
                "public static void NoteHit(WorldObject source, Player defender, int damageTaken, bool crit)");

            var worldEventCheck = body.IndexOf("if (evt == null)", StringComparison.Ordinal);
            var digsiteNullCheck = body.IndexOf("if (attacker.P_DigsiteEncounter == null)", StringComparison.Ordinal);
            var digsiteCall = body.IndexOf("MlDigsiteBossDamage.TryNoteHit(", StringComparison.Ordinal);
            var worldEventWork = body.IndexOf("evt.Spawner.BossGuid", StringComparison.Ordinal);

            Assert.IsTrue(worldEventCheck >= 0 && digsiteNullCheck > worldEventCheck, "the digsite branch sits behind the P_WorldEvent check");
            Assert.IsTrue(digsiteCall > digsiteNullCheck, "and behind its own single null check");
            Assert.IsTrue(digsiteCall < worldEventWork, "inside the no-world-event branch, not after the world event work");
        }

        // ---- spawn-path wiring (both paths identical) -----------------------------------------------------

        [TestMethod]
        public void Digsite_TrySpawn_applies_M1_then_spread_then_headcount_all_before_EnterWorld()
        {
            var trySpawn = PooledLootSourceText.MethodBody(
                PooledLootSourceText.Read("Source/ACE.Server/MlDigsite/MlDigsiteSpawner.cs"),
                "public static Creature TrySpawn(MlDigsiteEncounter encounter, MlDigsiteRole role, Random rng, int waveNumber = 1)");

            var m1 = trySpawn.IndexOf("ApplyMapEventBossMagicDefenseScale(creature);", StringComparison.Ordinal);
            var spread = trySpawn.IndexOf("MlMapEventScaling.ApplySpreadScaling(creature", StringComparison.Ordinal);
            var headcount = trySpawn.IndexOf("ApplySpawnScaling(creature, profiles.Count", StringComparison.Ordinal);
            var enter = trySpawn.IndexOf("creature.EnterWorld()", StringComparison.Ordinal);

            Assert.IsTrue(m1 >= 0 && spread > m1, "M1 runs before the defense floor");
            Assert.IsTrue(headcount > spread, "headcount raises multiply the downscaled creature");
            Assert.IsTrue(enter > headcount, "everything before EnterWorld");

            StringAssert.Contains(trySpawn, "MlDigsiteAudience.Sample(encounter.Anchor, MlDigsiteTunables.AudienceRadiusMetres)");
            StringAssert.Contains(trySpawn, "var keyRng = roleClass == MlMapEventRoleClass.Trash ? new Random(ThreadSafeRandom.Next(0, int.MaxValue - 1)) : null;",
                "only trash allocates a sampling Random, and it is its own - never the spawner's seeded rng");
            StringAssert.Contains(trySpawn, "ApplySpreadScaling(creature, roleClass, profiles, keyRng, spreadSettings");
        }

        [TestMethod]
        public void Relaria_PrepareBoss_runs_the_same_spread_scaling_as_a_Boss_after_M1()
        {
            var prepareBoss = PooledLootSourceText.MethodBody(
                PooledLootSourceText.Read("Source/ACE.Server/MlTreasure/MlRelariaSpawner.cs"),
                "public static void PrepareBoss(Creature boss, int bossWcid)");

            var m1 = prepareBoss.IndexOf("MlMapEventBossMagicDefense.ScaleInitLevel(", StringComparison.Ordinal);
            var spread = prepareBoss.IndexOf("MlMapEventScaling.ApplySpreadScaling(boss, MlMapEventRoleClass.Boss", StringComparison.Ordinal);

            Assert.IsTrue(m1 >= 0 && spread > m1, "M1 runs before the defense floor");
            StringAssert.Contains(prepareBoss, "MlDigsiteAudience.Sample(boss.Location, MlDigsiteTunables.AudienceRadiusMetres)");
        }

        // ---- helpers -------------------------------------------------------------------------------------

        private static MlDigsiteEncounter NewEncounter(MlDigsiteType type)
            => new MlDigsiteEncounter(1, 0x50000001, "Tester", type, new Position(0x00010064, 50f, 50f, 0f, 0f, 0f, 0f, 1f, 0), DateTime.UtcNow);

        private static Creature BuildCreature(uint skillInit = 400, uint quickness = 10, uint coordination = 10)
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.Creature,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.Level, 200 },
                    { PropertyInt.XpOverride, 800000 },
                    { PropertyInt.LuminanceAward, 1000 },
                },
                PropertiesAttribute = new Dictionary<PropertyAttribute, PropertiesAttribute>
                {
                    { PropertyAttribute.Strength, new PropertiesAttribute { InitLevel = 10 } },
                    { PropertyAttribute.Endurance, new PropertiesAttribute { InitLevel = 10 } },
                    { PropertyAttribute.Coordination, new PropertiesAttribute { InitLevel = coordination } },
                    { PropertyAttribute.Quickness, new PropertiesAttribute { InitLevel = quickness } },
                    { PropertyAttribute.Focus, new PropertiesAttribute { InitLevel = 10 } },
                    { PropertyAttribute.Self, new PropertiesAttribute { InitLevel = 10 } },
                },
                PropertiesAttribute2nd = new Dictionary<PropertyAttribute2nd, PropertiesAttribute2nd>
                {
                    { PropertyAttribute2nd.MaxHealth, new PropertiesAttribute2nd { InitLevel = 2000 } },
                },
                PropertiesSkill = new Dictionary<Skill, PropertiesSkill>
                {
                    { Skill.MeleeDefense, new PropertiesSkill { InitLevel = skillInit, SAC = SkillAdvancementClass.Trained } },
                    { Skill.MissileDefense, new PropertiesSkill { InitLevel = skillInit, SAC = SkillAdvancementClass.Trained } },
                    { Skill.MagicDefense, new PropertiesSkill { InitLevel = skillInit, SAC = SkillAdvancementClass.Trained } },
                    { Skill.HeavyWeapons, new PropertiesSkill { InitLevel = skillInit, SAC = SkillAdvancementClass.Trained } },
                },
            };

            var creature = new Creature(weenie, new ObjectGuid(nextGuid++));
            creature.Location = new Position(0x00010064, 50f, 50f, 0f, 0f, 0f, 0f, 1f, 0);

            return creature;
        }

        private static uint AttrPart(Creature c, Skill skill)
        {
            var s = c.GetCreatureSkill(skill);
            return s.Base - s.InitLevel - s.Ranks;
        }

        private static uint EffectiveDefense(Creature c, Skill skill) => c.GetCreatureSkill(skill).Base;

        private readonly struct CreatureSnapshot : IEquatable<CreatureSnapshot>
        {
            public readonly uint MeleeDef, MissileDef, MagicDef, Attack, MaxHealth;
            public readonly int? Xp, Lum, Rating;

            public CreatureSnapshot(Creature c)
            {
                MeleeDef = c.GetCreatureSkill(Skill.MeleeDefense).Base;
                MissileDef = c.GetCreatureSkill(Skill.MissileDefense).Base;
                MagicDef = c.GetCreatureSkill(Skill.MagicDefense).Base;
                Attack = c.GetCreatureSkill(Skill.HeavyWeapons).Base;
                MaxHealth = c.Health.MaxValue;
                Xp = c.XpOverride;
                Lum = c.LuminanceAward;
                Rating = c.DamageRating;
            }

            public bool Equals(CreatureSnapshot o) => MeleeDef == o.MeleeDef && MissileDef == o.MissileDef && MagicDef == o.MagicDef
                && Attack == o.Attack && MaxHealth == o.MaxHealth && Xp == o.Xp && Lum == o.Lum && Rating == o.Rating;

            public override bool Equals(object obj) => obj is CreatureSnapshot o && Equals(o);

            public override int GetHashCode() => HashCode.Combine(MeleeDef, MissileDef, MagicDef, Attack, MaxHealth, Xp, Lum, Rating);

            public override string ToString() => $"def {MeleeDef}/{MissileDef}/{MagicDef} atk {Attack} hp {MaxHealth} xp {Xp} lum {Lum} dr {Rating}";
        }

        private static CreatureSnapshot Snapshot(Creature c) => new CreatureSnapshot(c);

        /// <summary>
        /// Counts bounded integer draws by DELEGATING to an inner Random - never to base, which routes
        /// Next(int) through the virtual Sample() (see this repo's CLAUDE.md on instrumenting Random).
        /// </summary>
        private sealed class CountingRandom : Random
        {
            private readonly Random inner;

            public CountingRandom(int seed) { inner = new Random(seed); }

            public int Draws { get; private set; }

            public override int Next(int maxValue) { Draws++; return inner.Next(maxValue); }

            public override int Next(int minValue, int maxValue) { Draws++; return inner.Next(minValue, maxValue); }

            public override int Next() { Draws++; return inner.Next(); }

            public override double NextDouble() { Draws++; return inner.NextDouble(); }
        }
    }
}
