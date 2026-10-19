using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>The monsters group of the Hold settings.</summary>
    public partial class WorldEventHoldSettingsTests
    {
        [TestMethod]
        public void Monsters_WithNothingSet_ReadExactlyTheGoalsJson()
        {
            var goal = EnvoysGoal();
            var p = goal.Protect;

            using var scope = new HoldSettingsScope();

            CollectionAssert.AreEqual(new[] { 0.2, 0.15, 0.15, 0.3, 0.1, 0.1 }, p.WaveMix.Shares);
            Assert.AreEqual(0.6, p.SpecialWave(HoldWaveType.Runners).HealthMult);
            Assert.AreEqual(18, p.SpecialWave(HoldWaveType.Ranged).HoldDistance);
            Assert.AreEqual(3, p.EffectiveCorrupted.SelfMult);
            Assert.AreEqual(0x04001381u, p.EffectiveCorrupted.TintPaletteId);

            foreach (var role in Enum.GetValues(typeof(HoldDamageRole)).Cast<HoldDamageRole>())
            {
                Assert.IsNull(HoldSettings.RoleHealthMult(role), role.ToString());
                Assert.IsNull(HoldSettings.RoleAttackSpeedMult(role), role.ToString());
                Assert.AreEqual(0, HoldSettings.RoleLevelOffset(role), role.ToString());
            }

            Assert.IsNull(HoldSettings.CasterShareOfRanged());

            foreach (var s in HoldSettings.InGroup(HoldSettings.GroupMonsters).Where(s => !s.IsReused))
                Assert.IsFalse(HoldSettings.Describe(s, goal).Overridden, s.Key);
        }

        [TestMethod]
        public void Monsters_EveryRoleHasItsSettings_AndNothingIsDuplicated()
        {
            foreach (var role in HoldSettings.RoleNames)
            {
                foreach (var suffix in new[] { "health_mult", "player_damage_mult", "npc_damage_mult", "attack_speed_mult", "level_offset" })
                    Assert.IsTrue(HoldSettings.TryFind($"{role}_{suffix}", out _), $"{role}_{suffix}");
            }

            // The runner's and corrupted's health, and the runner's attack speed, are the goals.json values (no second setting).
            Assert.IsFalse(HoldSettings.TryFind("runner_health_mult", out var runnerHealth) && runnerHealth.IsNew);
            Assert.IsFalse(HoldSettings.TryFind("corrupted_health_mult", out var corruptedHealth) && corruptedHealth.IsNew);
            Assert.IsFalse(HoldSettings.TryFind("runner_attack_speed_mult", out var runnerSpeed) && runnerSpeed.IsNew);
        }

        [TestMethod]
        public void Monsters_WaveMixRunnerRangedAndCorruptedAreLive()
        {
            var goal = EnvoysGoal();
            var p = goal.Protect;

            using var scope = new HoldSettingsScope();

            scope.Set(HoldKeys.ShareRanged, "0");
            scope.Set(HoldKeys.ShareSeekers, "0.5");
            scope.Set(HoldKeys.MinRanged, "0");
            Assert.AreEqual(0, p.WaveMix.Ranged);
            Assert.AreEqual(0.5, p.WaveMix.Seekers);
            Assert.AreEqual(0, p.WaveMix.Minimums[3], "no ranged minimum once ranged is 0");

            var counts = WorldEventHoldRules.WaveMixCounts(10, p.WaveMix.Shares, p.WaveMix.Minimums, p.WaveMix.Maximums);
            Assert.AreEqual(0, counts[3], "no ranged in the wave");

            scope.Set(HoldKeys.RunnerSpeedMult, "2.5");
            scope.Set(HoldKeys.RangedKeepDistance, "25");
            Assert.AreEqual(2.5, p.SpecialWave(HoldWaveType.Runners).SpeedMult);
            Assert.AreEqual(25, p.SpecialWave(HoldWaveType.Ranged).HoldDistance);
            Assert.AreEqual(25, new SpecialWaveDef { TypeKind = HoldWaveType.Ranged }.HoldDistance, "a fallback ranged entry too");
            Assert.AreEqual(18, new SpecialWaveDef { TypeKind = HoldWaveType.Runners }.HoldDistance, "only the ranged entry takes it");

            scope.Set(HoldKeys.CorruptedSelfMult, "5");
            scope.Set(HoldKeys.CorruptedAssaultEveryWaves, "0");
            Assert.AreEqual(5, p.EffectiveCorrupted.SelfMult);
            Assert.IsFalse(p.EffectiveCorrupted.AssaultMode, "assaults off: corrupted come by share");
        }

        [TestMethod]
        public void Monsters_TintToggle()
        {
            var goal = EnvoysGoal();
            var c = goal.Protect.EffectiveCorrupted;

            using var scope = new HoldSettingsScope();

            scope.Set(HoldKeys.CorruptedTint, "none");
            Assert.AreEqual(0u, c.TintPaletteId, "color off");

            scope.Set(HoldKeys.CorruptedTint, "0x04001378");
            Assert.AreEqual(0x04001378u, c.TintPaletteId);

            scope.Set(HoldKeys.CorruptedTint, "pink");
            Assert.AreEqual(0x04001381u, c.TintPaletteId, "not a palette: goals.json's color stays");
            Assert.IsTrue(HoldSettings.TryFind(HoldKeys.CorruptedTint, out var s));
            StringAssert.Contains(HoldSettings.Check(s, "pink"), "not a palette");
        }

        [TestMethod]
        public void Monsters_RoleMultipliersResolveLive()
        {
            using var scope = new HoldSettingsScope();

            var c = TestCreatures.CreateAttacker();
            c.WorldEventHoldRole = HoldDamageRole.Caster;

            Assert.AreEqual(1.0, HoldSettings.PlayerDamageMultFor(c));
            Assert.AreEqual(1.0, HoldSettings.NpcDamageMultFor(c));

            scope.Set("caster_player_damage_mult", "0.5");
            scope.Set("caster_npc_damage_mult", "0");
            Assert.AreEqual(0.5, HoldSettings.PlayerDamageMultFor(c));
            Assert.AreEqual(0.0, HoldSettings.NpcDamageMultFor(c), "0 on purpose: casters never hurt the knights");

            c.WorldEventHoldRole = HoldDamageRole.Melee;
            Assert.AreEqual(1.0, HoldSettings.PlayerDamageMultFor(c), "another role is untouched");

            c.WorldEventHoldRole = HoldDamageRole.None;
            Assert.AreEqual(1.0, HoldSettings.PlayerDamageMultFor(c));

            scope.Set("melee_health_mult", "2");
            scope.Set("ranged_attack_speed_mult", "9");
            Assert.AreEqual(2.0, HoldSettings.RoleHealthMult(HoldDamageRole.Melee));
            Assert.IsNull(HoldSettings.RoleHealthMult(HoldDamageRole.Runner), "the runner's health is runner_health_mult (the plan)");
            Assert.AreEqual(3.0, HoldSettings.RoleAttackSpeedMult(HoldDamageRole.Ranged), "clamped");
        }

        [TestMethod]
        public void Monsters_DamageRoleFromTheWaveRole()
        {
            Assert.AreEqual(HoldDamageRole.Melee, WorldEventSpawner.DamageRoleFor(HoldRole.Breach, null));
            Assert.AreEqual(HoldDamageRole.Melee, WorldEventSpawner.DamageRoleFor(HoldRole.Seeker, null));
            Assert.AreEqual(HoldDamageRole.Melee, WorldEventSpawner.DamageRoleFor(HoldRole.Hunter, null));
            Assert.AreEqual(HoldDamageRole.Runner, WorldEventSpawner.DamageRoleFor(HoldRole.Runner, null));
            Assert.AreEqual(HoldDamageRole.Corrupted, WorldEventSpawner.DamageRoleFor(HoldRole.Corrupted, null));
            Assert.AreEqual(HoldDamageRole.Ranged, WorldEventSpawner.DamageRoleFor(HoldRole.Ranged, null), "no creature to read: an archer");

            Assert.IsTrue(WorldEventRangedMembers.IsCasterAttack(false, false, 0.3));
            Assert.IsFalse(WorldEventRangedMembers.IsCasterAttack(true, false, 0.3), "a bow and spells: an archer");
            Assert.IsFalse(WorldEventRangedMembers.IsCasterAttack(false, false, 0.05), "a rare bolt is not a caster");
        }

        [TestMethod]
        public void Monsters_CasterShareOfRanged()
        {
            var trash = Enumerable.Repeat(1u, 200).ToList();
            var roles = Enumerable.Repeat(HoldRole.Ranged, 200).ToList();
            var pool = new List<uint> { 10, 11, 20, 21 };
            Func<uint, bool> isCaster = w => w >= 20;

            var none = WorldEventHoldRules.ReplaceRangedSlots(trash, roles, pool, new Random(3), isCaster, null);
            CollectionAssert.AreEqual(WorldEventHoldRules.ReplaceRangedSlots(trash, roles, pool, new Random(3)), none, "not set: exactly the plain draw");

            Assert.IsTrue(WorldEventHoldRules.ReplaceRangedSlots(trash, roles, pool, new Random(3), isCaster, 1.0).All(isCaster), "all casters");
            Assert.IsTrue(WorldEventHoldRules.ReplaceRangedSlots(trash, roles, pool, new Random(3), isCaster, 0.0).All(w => !isCaster(w)), "all archers");

            var half = WorldEventHoldRules.ReplaceRangedSlots(trash, roles, pool, new Random(3), isCaster, 0.5).Count(isCaster);
            Assert.IsTrue(half > 70 && half < 130, $"about half: {half}");

            var onlyArchers = new List<uint> { 10, 11 };
            CollectionAssert.AreEqual(WorldEventHoldRules.ReplaceRangedSlots(trash, roles, onlyArchers, new Random(3)),
                WorldEventHoldRules.ReplaceRangedSlots(trash, roles, onlyArchers, new Random(3), isCaster, 1.0), "one kind only: the plain draw");
        }

        [TestMethod]
        public void Monsters_LevelOffsets_RedrawOnlyOffsetSlots()
        {
            var trash = new List<uint> { 1, 2, 3, 4 };
            var keep = new HashSet<int> { 3 };

            var redrawn = WorldEventHoldRules.RedrawForLevelOffsets(trash, keep, i => i == 0 || i == 3 ? 10 : 0,
                i => new List<uint> { 99 }, new Random(1), out var moved);

            CollectionAssert.AreEqual(new List<uint> { 99, 2, 3, 4 }, redrawn, "slot 0 moved; slot 3 is kept (an elite)");
            CollectionAssert.AreEquivalent(new[] { 0 }, moved.Keys.ToList());
            Assert.AreEqual(10, moved[0]);

            var empty = WorldEventHoldRules.RedrawForLevelOffsets(trash, null, i => -5, i => new List<uint>(), new Random(1), out var none);
            CollectionAssert.AreEqual(trash, empty, "nobody in the moved band: unchanged");
            Assert.AreEqual(0, none.Count);

            var band = WorldEventHoldRules.ShiftBand(new LevelBand(150, 180), 120);
            Assert.AreEqual(270, band.Low);
            Assert.AreEqual(275, band.High, "kept within 1 to 275");
            Assert.AreEqual(1, WorldEventHoldRules.ShiftBand(new LevelBand(10, 20), -50).Low);

            Assert.AreEqual(160, WorldEventSpawner.MovedSlotLevel(150, 10));
            Assert.AreEqual(150, WorldEventSpawner.MovedSlotLevel(150, 0));
        }
    }

    public partial class WorldEventHoldTests
    {
        [TestMethod]
        public void RoleNpcDamageMult_ScalesThatRolesHitsOnAKnight()
        {
            var (evt, protect) = BuildTimeScaledRun();
            protect.MinDeathSeconds = 0;

            var melee = BuildHoldCreature();
            melee.P_WorldEvent = evt;
            melee.WorldEventHoldRole = HoldDamageRole.Melee;

            var plain = BuildFreshTarget(evt).ScaleWorldEventProtectedHit(melee, 30f, T0);

            using var scope = new HoldSettingsScope();
            scope.Set("melee_npc_damage_mult", "3");

            Assert.AreEqual(plain * 3, BuildFreshTarget(evt).ScaleWorldEventProtectedHit(melee, 30f, T0), 1.0);

            melee.WorldEventHoldRole = HoldDamageRole.Runner;
            Assert.AreEqual(plain, BuildFreshTarget(evt).ScaleWorldEventProtectedHit(melee, 30f, T0), 1.0, "another role's setting does nothing");
        }
    }
}
